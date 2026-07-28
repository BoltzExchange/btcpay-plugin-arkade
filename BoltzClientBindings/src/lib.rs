mod store;

use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};

use async_trait::async_trait;
use boltz_client::{
    AlchemyConfig, Asset, BoltzConfig, BoltzError, BoltzEventListener, BoltzService, BoltzSwap,
    BoltzSwapEvent, BoltzSwapStatus, BridgeKind, DestinationOption, SwapLimits,
};
use store::ForeignStorageAdapter;
use tokio::runtime::{Builder, Runtime};
use zeroize::Zeroizing;

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
/// - `upsert_swap` must be durable before returning.
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
    pub seed: Vec<u8>,
    pub referral_id: String,
    pub slippage_bps: u32,
    pub api_url: Option<String>,
    pub gas_sponsor_url: Option<String>,
    pub arbitrum_rpc_url: Option<String>,
    pub solana_rpc_url: Option<String>,
    pub disable_delivery_polling: bool,
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
}

#[derive(Clone, Debug, uniffi::Record)]
pub struct BindingCreatedSwap {
    pub swap_id: String,
    pub invoice: String,
    pub invoice_amount_sats: u64,
    pub output_amount: u64,
    pub boltz_fee_sats: u64,
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
    pub expected_output_amount: u64,
    pub lockup_tx_id: Option<String>,
    pub claim_tx_hash: Option<String>,
    pub delivered_amount: Option<u64>,
    pub bridge_ref: Option<String>,
}

#[derive(Clone, Debug, uniffi::Record)]
pub struct BindingQuoteDegraded {
    pub swap_id: String,
    pub expected_usd: u64,
    pub quoted_usd: u64,
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
    events: Mutex<HashMap<String, BindingQuoteDegraded>>,
}

impl EventQueue {
    fn new() -> Self {
        Self {
            events: Mutex::new(HashMap::new()),
        }
    }

    fn push(&self, event: BindingQuoteDegraded) {
        let mut events = self
            .events
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        events.insert(event.swap_id.clone(), event);
    }

    fn drain(&self) -> Vec<BindingQuoteDegraded> {
        let mut events = self
            .events
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        events.drain().map(|(_, event)| event).collect()
    }
}

struct QueueListener(Arc<EventQueue>);

#[async_trait]
impl BoltzEventListener for QueueListener {
    async fn on_event(&self, event: BoltzSwapEvent) {
        // Plain swap updates are not forwarded: the C# consumer follows swap
        // progress through the durable poll. Keep only the latest actionable
        // quote degradation for each swap until the host drains it.
        if let BoltzSwapEvent::QuoteDegraded {
            swap,
            expected_usd,
            quoted_usd,
        } = event
        {
            self.0.push(BindingQuoteDegraded {
                swap_id: swap.id,
                expected_usd,
                quoted_usd,
            });
        }
    }
}

#[derive(uniffi::Object)]
pub struct BoltzClient {
    runtime: Runtime,
    service: Arc<BoltzService>,
    events: Arc<EventQueue>,
    shutdown: AtomicBool,
}

#[uniffi::export]
impl BoltzClient {
    #[uniffi::constructor]
    pub fn new(
        config: ClientConfig,
        storage: Arc<dyn SwapStorage>,
    ) -> Result<Arc<Self>, BindingError> {
        // Move the seed into a drop-zeroizing guard immediately so EVERY exit
        // from this constructor — including early error returns — wipes it.
        let core_config = to_core_config(&config);
        let seed = Zeroizing::new(config.seed);
        let runtime = Builder::new_multi_thread()
            .enable_all()
            .thread_name("boltz-client")
            .build()
            .map_err(|error| BindingError::operation("runtime", error.to_string()))?;
        let store = Arc::new(ForeignStorageAdapter::new(storage));
        let service = runtime.block_on(BoltzService::new(core_config, seed.as_slice(), store));
        drop(seed);
        let service = Arc::new(service?);
        let events = Arc::new(EventQueue::new());
        runtime.block_on(service.add_event_listener(Box::new(QueueListener(events.clone()))));

        Ok(Arc::new(Self {
            runtime,
            service,
            events,
            shutdown: AtomicBool::new(false),
        }))
    }

    pub async fn resume_swaps(&self) -> Result<u64, BindingError> {
        let service = self.service.clone();
        self.run(async move { service.resume_swaps().await })
            .await
            .map(|swap_ids| swap_ids.len() as u64)
    }

    pub async fn get_swap(&self, swap_id: String) -> Result<Option<BindingSwap>, BindingError> {
        let service = self.service.clone();
        self.run(async move { service.get_swap(&swap_id).await })
            .await
            .map(|swap| swap.map(Into::into))
    }

    pub async fn create_reverse_swap_from_sats(
        &self,
        destination: String,
        chain: String,
        asset: BindingAsset,
        invoice_amount_sats: u64,
    ) -> Result<BindingCreatedSwap, BindingError> {
        let service = self.service.clone();
        self.run(async move {
            let prepared = service
                .prepare_reverse_swap_from_sats(
                    &destination,
                    &chain,
                    asset.into(),
                    invoice_amount_sats,
                    None,
                )
                .await?;
            let created = service.create_reverse_swap(&prepared).await?;
            Ok(BindingCreatedSwap {
                swap_id: created.swap_id,
                invoice: created.invoice,
                invoice_amount_sats: created.invoice_amount_sats,
                output_amount: prepared.output_amount,
                boltz_fee_sats: prepared.boltz_fee_sats,
            })
        })
        .await
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
    pub async fn accept_degraded_quote(&self, swap_id: String) -> Result<(), BindingError> {
        let service = self.service.clone();
        self.run(async move { service.accept_degraded_quote(&swap_id).await })
            .await
            .map(|_| ())
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

    pub fn drain_quote_degradations(&self) -> Vec<BindingQuoteDegraded> {
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
            expected_output_amount: value.expected_output_amount,
            lockup_tx_id: value.lockup_tx_id,
            claim_tx_hash: value.claim_tx_hash,
            delivered_amount: value.delivered_amount,
            bridge_ref: value.bridge_ref,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn event_queue_keeps_latest_degradation_per_swap() {
        let queue = EventQueue::new();
        queue.push(BindingQuoteDegraded {
            swap_id: "swap-a".to_string(),
            expected_usd: 10_000,
            quoted_usd: 9_500,
        });
        queue.push(BindingQuoteDegraded {
            swap_id: "swap-a".to_string(),
            expected_usd: 10_000,
            quoted_usd: 9_000,
        });
        queue.push(BindingQuoteDegraded {
            swap_id: "swap-b".to_string(),
            expected_usd: 20_000,
            quoted_usd: 19_000,
        });

        let drained: HashMap<_, _> = queue
            .drain()
            .into_iter()
            .map(|event| (event.swap_id.clone(), event))
            .collect();

        assert_eq!(drained.len(), 2);
        assert_eq!(drained["swap-a"].quoted_usd, 9_000);
        assert_eq!(drained["swap-b"].quoted_usd, 19_000);
        assert!(queue.drain().is_empty());
    }
}

uniffi::setup_scaffolding!();
