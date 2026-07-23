mod store;

use std::collections::VecDeque;
use std::fmt;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};

use async_trait::async_trait;
use boltz_client::{
    AlchemyConfig, Asset, BoltzConfig, BoltzError, BoltzEventListener, BoltzService, BoltzSwap,
    BoltzSwapEvent, BoltzSwapStatus, BridgeKind, CreatedSwap, DestinationOption, PreparedSwap,
    SwapLimits,
};
use store::ForeignStorageAdapter;
use tokio::runtime::{Builder, Runtime};
use zeroize::Zeroizing;

const API_VERSION: &str = "0.1.0";
const MAX_QUEUED_EVENTS: usize = 256;

/// Per-wallet swap persistence, implemented by the C# host (the BTCPay
/// plugin's EF-backed store) and passed into the [`BoltzClient`] constructor.
/// One `BoltzClient` gets exactly one storage instance — there is no scope
/// parameter; wallet separation is the host's responsibility.
///
/// Contract for implementors:
/// - Methods are synchronous and may block on database I/O; rust invokes them
///   from a blocking-worker thread of its own runtime, NEVER from a .NET
///   thread the host controls. Do not touch thread-affine state and do not
///   call back into the same `BoltzClient` from inside a storage method.
/// - `swap_json` is opaque; persist it byte-for-byte. `status`/`is_terminal`
///   are denormalized convenience columns derived from the same swap.
/// - `upsert_swap` must be durable before returning: in seedless mode the row
///   carries the swap's only copy of its secrets.
/// - `next_key_index` must be strictly monotonic per wallet across restarts
///   and processes (atomicity is the host's job): a regressed counter would
///   re-derive preimages of past swaps, enabling fund theft.
/// - Failures must be signalled by throwing the generated binding exception;
///   they surface to swap logic as store errors.
#[uniffi::export(with_foreign)]
pub trait SwapStorage: Send + Sync {
    fn upsert_swap(
        &self,
        swap_id: String,
        swap_json: String,
        status: String,
        is_terminal: bool,
    ) -> Result<(), BindingError>;
    fn get_swap(&self, swap_id: String) -> Result<Option<String>, BindingError>;
    fn list_active_swaps(&self) -> Result<Vec<String>, BindingError>;
    fn next_key_index(&self) -> Result<u32, BindingError>;
}

#[derive(Clone, uniffi::Record)]
pub struct ClientConfig {
    pub seed: Option<Vec<u8>>,
    pub referral_id: String,
    pub slippage_bps: u32,
    pub api_url: Option<String>,
    pub gas_sponsor_url: Option<String>,
    pub arbitrum_rpc_url: Option<String>,
    pub solana_rpc_url: Option<String>,
    pub disable_delivery_polling: bool,
}

struct Redacted;

impl fmt::Debug for Redacted {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("<redacted>")
    }
}

impl fmt::Debug for ClientConfig {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("ClientConfig")
            .field("seed", &self.seed.as_ref().map(|_| Redacted))
            .field("referral_id", &self.referral_id)
            .field("slippage_bps", &self.slippage_bps)
            .field("api_url", &self.api_url)
            .field("gas_sponsor_url", &self.gas_sponsor_url)
            .field("arbitrum_rpc_url", &self.arbitrum_rpc_url)
            .field("solana_rpc_url", &self.solana_rpc_url)
            .field("disable_delivery_polling", &self.disable_delivery_polling)
            .finish()
    }
}

#[derive(Clone, Debug, uniffi::Record)]
pub struct Capabilities {
    pub api_version: String,
    pub upstream_revision: String,
    pub seeded: bool,
}

#[derive(Clone, Copy, Debug, uniffi::Enum)]
pub enum BindingAsset {
    Usdt,
    Usdt0,
    Usdc,
}

#[derive(Clone, Copy, Debug, uniffi::Enum)]
pub enum BindingBridgeKind {
    Direct,
    Oft,
    Cctp,
}

#[derive(Clone, Debug, uniffi::Enum)]
pub enum BindingSwapStatus {
    Created,
    InvoicePaid,
    TbtcLocked,
    Claiming,
    Settling,
    Completed,
    Failed { reason: String },
    Expired,
}

#[derive(Clone, Debug, uniffi::Record)]
pub struct BindingDestination {
    pub chain_label: String,
    pub asset: BindingAsset,
    pub bridge_kind: BindingBridgeKind,
}

#[derive(Clone, Debug, uniffi::Record)]
pub struct BindingPreparedSwap {
    pub destination_address: String,
    pub destination_chain: String,
    pub asset: BindingAsset,
    pub bridge_kind: BindingBridgeKind,
    pub output_amount: u64,
    pub invoice_amount_sats: u64,
    pub boltz_fee_sats: u64,
    pub estimated_onchain_amount: u64,
    pub slippage_bps: u32,
    pub pair_hash: String,
    pub expires_at: u64,
}

#[derive(Clone, Debug, uniffi::Record)]
pub struct BindingCreatedSwap {
    pub swap_id: String,
    pub invoice: String,
    pub invoice_amount_sats: u64,
    pub timeout_block_height: u64,
}

#[derive(Clone, Debug, uniffi::Record)]
pub struct BindingSwapLimits {
    pub min_sats: u64,
    pub max_sats: u64,
}

#[derive(Clone, Debug, uniffi::Record)]
pub struct BindingSwap {
    pub id: String,
    pub status: BindingSwapStatus,
    pub bridge_kind: BindingBridgeKind,
    pub chain_id: u64,
    pub claim_address: String,
    pub destination_address: String,
    pub destination_chain: String,
    pub asset: BindingAsset,
    pub refund_address: String,
    pub erc20swap_address: String,
    pub router_address: String,
    pub invoice: String,
    pub invoice_amount_sats: u64,
    pub onchain_amount: u64,
    pub expected_output_amount: u64,
    pub slippage_bps: u32,
    pub timeout_block_height: u64,
    pub lockup_tx_id: Option<String>,
    pub claim_tx_hash: Option<String>,
    pub pending_call_id: Option<String>,
    pub delivered_amount: Option<u64>,
    pub bridge_ref: Option<String>,
    pub created_at: u64,
    pub updated_at: u64,
}

#[derive(Clone, Debug, uniffi::Enum)]
pub enum BindingEvent {
    QuoteDegraded {
        swap: BindingSwap,
        expected_usd: u64,
        quoted_usd: u64,
    },
    ResyncRequired,
}

#[derive(Debug, thiserror::Error, uniffi::Error)]
pub enum BindingError {
    #[error("{message}")]
    Operation { code: String, message: String },
}

impl BindingError {
    fn operation(code: impl Into<String>, message: impl Into<String>) -> Self {
        Self::Operation {
            code: code.into(),
            message: message.into(),
        }
    }
}

impl From<BoltzError> for BindingError {
    fn from(error: BoltzError) -> Self {
        let code = match &error {
            BoltzError::Api { .. } => "api",
            BoltzError::Evm { .. } => "evm",
            BoltzError::WebSocket(_) => "websocket",
            BoltzError::Signing(_) => "signing",
            BoltzError::Store(_) => "store",
            BoltzError::SwapExpired { .. } => "swap_expired",
            BoltzError::SwapFailed { .. } => "swap_failed",
            BoltzError::QuoteExpired => "quote_expired",
            BoltzError::AmountOutOfRange { .. } => "amount_out_of_range",
            BoltzError::InvalidQuote(_) => "invalid_quote",
            BoltzError::QuoteDegradedBeyondSlippage { .. } => "quote_degraded",
            BoltzError::ClaimBroadcastUnconfirmed { .. } => "claim_unconfirmed",
            BoltzError::DuplicatePreimage => "duplicate_preimage",
            BoltzError::Generic(_) => "generic",
        };
        Self::operation(code, error.to_string())
    }
}

struct EventQueue {
    events: Mutex<VecDeque<BindingEvent>>,
    overflowed: AtomicBool,
}

impl EventQueue {
    fn new() -> Self {
        Self {
            events: Mutex::new(VecDeque::with_capacity(MAX_QUEUED_EVENTS)),
            overflowed: AtomicBool::new(false),
        }
    }

    fn push(&self, event: BindingEvent) {
        let mut events = self
            .events
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        if events.len() == MAX_QUEUED_EVENTS {
            events.pop_front();
            self.overflowed.store(true, Ordering::Release);
        }
        events.push_back(event);
    }

    fn drain(&self) -> Vec<BindingEvent> {
        let mut events = self
            .events
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        let mut drained = Vec::with_capacity(events.len() + 1);
        if self.overflowed.swap(false, Ordering::AcqRel) {
            drained.push(BindingEvent::ResyncRequired);
        }
        drained.extend(events.drain(..));
        drained
    }
}

struct QueueListener(Arc<EventQueue>);

#[async_trait]
impl BoltzEventListener for QueueListener {
    async fn on_event(&self, event: BoltzSwapEvent) {
        // Plain swap updates are not forwarded: the C# consumer follows swap
        // progress through the durable poll, and queueing every update only
        // overflowed the queue with unread events. Only quote degradation and
        // the overflow-driven resync marker cross the FFI.
        if let BoltzSwapEvent::QuoteDegraded {
            swap,
            expected_usd,
            quoted_usd,
        } = event
        {
            self.0.push(BindingEvent::QuoteDegraded {
                swap: swap.into(),
                expected_usd,
                quoted_usd,
            });
        }
    }
}

#[derive(uniffi::Object)]
pub struct BoltzClient {
    runtime: Arc<Runtime>,
    service: Arc<BoltzService>,
    events: Arc<EventQueue>,
    seeded: bool,
    shutdown: AtomicBool,
}

#[uniffi::export]
impl BoltzClient {
    #[uniffi::constructor]
    pub fn new(
        mut config: ClientConfig,
        storage: Arc<dyn SwapStorage>,
    ) -> Result<Arc<Self>, BindingError> {
        // Move the seed into a drop-zeroizing guard immediately so EVERY exit
        // from this constructor — including early error returns — wipes it.
        let seed = config.seed.take().map(Zeroizing::new);
        let seeded = seed.is_some();
        let runtime = Arc::new(
            Builder::new_multi_thread()
                .enable_all()
                .thread_name("boltz-client")
                .build()
                .map_err(|error| BindingError::operation("runtime", error.to_string()))?,
        );
        let store = Arc::new(ForeignStorageAdapter::new(storage));
        let core_config = to_core_config(&config);
        let service = runtime.block_on(async {
            match seed.as_deref() {
                Some(seed) => BoltzService::new(core_config, seed, store.clone()).await,
                None => BoltzService::new_seedless(core_config, store).await,
            }
        });
        drop(seed);
        let service = Arc::new(service?);
        let events = Arc::new(EventQueue::new());
        runtime.block_on(service.add_event_listener(Box::new(QueueListener(events.clone()))));

        Ok(Arc::new(Self {
            runtime,
            service,
            events,
            seeded,
            shutdown: AtomicBool::new(false),
        }))
    }

    pub fn get_capabilities(&self) -> Capabilities {
        Capabilities {
            api_version: API_VERSION.to_string(),
            upstream_revision: "ef036b3b348f042d70c0141aaaf421c3eda075eb".to_string(),
            seeded: self.seeded,
        }
    }

    pub async fn resume_swaps(&self) -> Result<Vec<String>, BindingError> {
        let service = self.service.clone();
        self.run(async move { service.resume_swaps().await }).await
    }

    pub async fn get_swap(&self, swap_id: String) -> Result<Option<BindingSwap>, BindingError> {
        let service = self.service.clone();
        self.run(async move { service.get_swap(&swap_id).await })
            .await
            .map(|swap| swap.map(Into::into))
    }

    pub async fn prepare_from_sats(
        &self,
        destination: String,
        chain: String,
        asset: BindingAsset,
        invoice_amount_sats: u64,
        max_slippage_bps: Option<u32>,
    ) -> Result<BindingPreparedSwap, BindingError> {
        let service = self.service.clone();
        self.run(async move {
            service
                .prepare_reverse_swap_from_sats(
                    &destination,
                    &chain,
                    asset.into(),
                    invoice_amount_sats,
                    max_slippage_bps,
                )
                .await
        })
        .await
        .map(Into::into)
    }

    pub async fn create_reverse_swap(
        &self,
        prepared: BindingPreparedSwap,
    ) -> Result<BindingCreatedSwap, BindingError> {
        let service = self.service.clone();
        self.run(async move { service.create_reverse_swap(&prepared.into()).await })
            .await
            .map(Into::into)
    }

    /// Accept a degraded DEX quote and force the claim to proceed with the
    /// current quote (on-chain slippage protection still applies). Call after
    /// draining a `QuoteDegraded` event for the swap.
    ///
    /// Not idempotent: the swap must be in `TbtcLocked` or `Claiming` status,
    /// otherwise this fails with code `generic` (unknown swap ids fail with
    /// code `store`) — guard on the swap's status before calling. If the
    /// forced claim itself fails, the error is surfaced and the swap stays in
    /// `Claiming` for the manager's retry; calling again then is safe.
    pub async fn accept_degraded_quote(&self, swap_id: String) -> Result<BindingSwap, BindingError> {
        let service = self.service.clone();
        self.run(async move { service.accept_degraded_quote(&swap_id).await })
            .await
            .map(Into::into)
    }

    pub fn destinations_accepting(&self, address: String) -> Vec<BindingDestination> {
        self.service
            .destinations_accepting(&address)
            .into_iter()
            .map(Into::into)
            .collect()
    }

    pub async fn get_limits(&self) -> Result<BindingSwapLimits, BindingError> {
        let service = self.service.clone();
        self.run(async move { service.get_limits().await })
            .await
            .map(Into::into)
    }

    pub fn drain_events(&self) -> Vec<BindingEvent> {
        self.events.drain()
    }

    pub async fn shutdown(&self) -> Result<(), BindingError> {
        if self.shutdown.swap(true, Ordering::AcqRel) {
            return Ok(());
        }
        let service = self.service.clone();
        self.run_infallible(async move { service.shutdown().await })
            .await
    }
}

impl BoltzClient {
    async fn run<T, F>(&self, operation: F) -> Result<T, BindingError>
    where
        T: Send + 'static,
        F: std::future::Future<Output = Result<T, BoltzError>> + Send + 'static,
    {
        self.runtime
            .spawn(operation)
            .await
            .map_err(|error| BindingError::operation("runtime_join", error.to_string()))?
            .map_err(Into::into)
    }

    async fn run_infallible<F>(&self, operation: F) -> Result<(), BindingError>
    where
        F: std::future::Future<Output = ()> + Send + 'static,
    {
        self.runtime
            .spawn(operation)
            .await
            .map_err(|error| BindingError::operation("runtime_join", error.to_string()))
    }
}

fn to_core_config(config: &ClientConfig) -> BoltzConfig {
    let mut core = BoltzConfig::mainnet(config.referral_id.clone());
    core.slippage_bps = config.slippage_bps;
    if let Some(value) = &config.api_url {
        core.api_url = value.clone();
    }
    if let Some(value) = &config.gas_sponsor_url {
        core.alchemy_config = AlchemyConfig {
            gas_sponsor_url: value.clone(),
        };
    }
    if let Some(value) = &config.arbitrum_rpc_url {
        core.arbitrum_rpc_url = value.clone();
    }
    if let Some(value) = &config.solana_rpc_url {
        core.solana_rpc_url = value.clone();
    }
    if config.disable_delivery_polling {
        core.delivery_poll_interval_secs = None;
    }
    core
}

impl From<BindingAsset> for Asset {
    fn from(value: BindingAsset) -> Self {
        match value {
            BindingAsset::Usdt => Self::Usdt,
            BindingAsset::Usdt0 => Self::Usdt0,
            BindingAsset::Usdc => Self::Usdc,
        }
    }
}

impl From<Asset> for BindingAsset {
    fn from(value: Asset) -> Self {
        match value {
            Asset::Usdt => Self::Usdt,
            Asset::Usdt0 => Self::Usdt0,
            Asset::Usdc => Self::Usdc,
        }
    }
}

impl From<BridgeKind> for BindingBridgeKind {
    fn from(value: BridgeKind) -> Self {
        match value {
            BridgeKind::Direct => Self::Direct,
            BridgeKind::Oft => Self::Oft,
            BridgeKind::Cctp => Self::Cctp,
        }
    }
}

impl From<BindingBridgeKind> for BridgeKind {
    fn from(value: BindingBridgeKind) -> Self {
        match value {
            BindingBridgeKind::Direct => Self::Direct,
            BindingBridgeKind::Oft => Self::Oft,
            BindingBridgeKind::Cctp => Self::Cctp,
        }
    }
}

impl From<BoltzSwapStatus> for BindingSwapStatus {
    fn from(value: BoltzSwapStatus) -> Self {
        match value {
            BoltzSwapStatus::Created => Self::Created,
            BoltzSwapStatus::InvoicePaid => Self::InvoicePaid,
            BoltzSwapStatus::TbtcLocked => Self::TbtcLocked,
            BoltzSwapStatus::Claiming => Self::Claiming,
            BoltzSwapStatus::Settling => Self::Settling,
            BoltzSwapStatus::Completed => Self::Completed,
            BoltzSwapStatus::Failed { reason } => Self::Failed { reason },
            BoltzSwapStatus::Expired => Self::Expired,
        }
    }
}

impl From<DestinationOption> for BindingDestination {
    fn from(value: DestinationOption) -> Self {
        Self {
            chain_label: value.chain_label,
            asset: value.asset.into(),
            bridge_kind: value.bridge_kind.into(),
        }
    }
}

impl From<PreparedSwap> for BindingPreparedSwap {
    fn from(value: PreparedSwap) -> Self {
        Self {
            destination_address: value.destination_address,
            destination_chain: value.destination_chain,
            asset: value.asset.into(),
            bridge_kind: value.bridge_kind.into(),
            output_amount: value.output_amount,
            invoice_amount_sats: value.invoice_amount_sats,
            boltz_fee_sats: value.boltz_fee_sats,
            estimated_onchain_amount: value.estimated_onchain_amount,
            slippage_bps: value.slippage_bps,
            pair_hash: value.pair_hash,
            expires_at: value.expires_at,
        }
    }
}

impl From<BindingPreparedSwap> for PreparedSwap {
    fn from(value: BindingPreparedSwap) -> Self {
        Self {
            destination_address: value.destination_address,
            destination_chain: value.destination_chain,
            asset: value.asset.into(),
            bridge_kind: value.bridge_kind.into(),
            output_amount: value.output_amount,
            invoice_amount_sats: value.invoice_amount_sats,
            boltz_fee_sats: value.boltz_fee_sats,
            estimated_onchain_amount: value.estimated_onchain_amount,
            slippage_bps: value.slippage_bps,
            pair_hash: value.pair_hash,
            expires_at: value.expires_at,
        }
    }
}

impl From<CreatedSwap> for BindingCreatedSwap {
    fn from(value: CreatedSwap) -> Self {
        Self {
            swap_id: value.swap_id,
            invoice: value.invoice,
            invoice_amount_sats: value.invoice_amount_sats,
            timeout_block_height: value.timeout_block_height,
        }
    }
}

impl From<SwapLimits> for BindingSwapLimits {
    fn from(value: SwapLimits) -> Self {
        Self {
            min_sats: value.min_sats,
            max_sats: value.max_sats,
        }
    }
}

impl From<BoltzSwap> for BindingSwap {
    fn from(value: BoltzSwap) -> Self {
        Self {
            id: value.id,
            status: value.status.into(),
            bridge_kind: value.bridge_kind.into(),
            chain_id: value.chain_id,
            claim_address: value.claim_address,
            destination_address: value.destination_address,
            destination_chain: value.destination_chain,
            asset: value.asset.into(),
            refund_address: value.refund_address,
            erc20swap_address: value.erc20swap_address,
            router_address: value.router_address,
            invoice: value.invoice,
            invoice_amount_sats: value.invoice_amount_sats,
            onchain_amount: value.onchain_amount,
            expected_output_amount: value.expected_output_amount,
            slippage_bps: value.slippage_bps,
            timeout_block_height: value.timeout_block_height,
            lockup_tx_id: value.lockup_tx_id,
            claim_tx_hash: value.claim_tx_hash,
            pending_call_id: value.pending_call_id,
            delivered_amount: value.delivered_amount,
            bridge_ref: value.bridge_ref,
            created_at: value.created_at,
            updated_at: value.updated_at,
        }
    }
}

uniffi::setup_scaffolding!();
