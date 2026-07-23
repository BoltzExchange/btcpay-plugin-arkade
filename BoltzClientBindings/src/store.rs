use std::sync::Arc;

use async_trait::async_trait;
use boltz_client::{BoltzError, BoltzStorage, BoltzSwap, BoltzSwapStatus, DerivedKeyStore};

use crate::{BindingError, SwapStorage};

/// Stable status labels for the C# convenience columns. The authoritative
/// state (including failure reasons) lives inside the opaque swap json.
pub fn status_label(status: &BoltzSwapStatus) -> &'static str {
    match status {
        BoltzSwapStatus::Created => "created",
        BoltzSwapStatus::InvoicePaid => "invoice_paid",
        BoltzSwapStatus::TbtcLocked => "tbtc_locked",
        BoltzSwapStatus::Claiming => "claiming",
        BoltzSwapStatus::Settling => "settling",
        BoltzSwapStatus::Completed => "completed",
        BoltzSwapStatus::Failed { .. } => "failed",
        BoltzSwapStatus::Expired => "expired",
    }
}

/// Thin adapter exposing a foreign [`SwapStorage`] implementation (the BTCPay
/// plugin's EF-backed store) to the boltz-client core as
/// `BoltzStorage` + `DerivedKeyStore`.
///
/// The foreign methods are synchronous and are expected to block on database
/// I/O, so every call is dispatched through `spawn_blocking` — the tokio
/// runtime driving swaps never blocks on the C# side.
pub struct ForeignStorageAdapter {
    storage: Arc<dyn SwapStorage>,
}

impl ForeignStorageAdapter {
    pub fn new(storage: Arc<dyn SwapStorage>) -> Self {
        Self { storage }
    }

    async fn dispatch<T, F>(&self, operation: F) -> Result<T, BoltzError>
    where
        T: Send + 'static,
        F: FnOnce(&dyn SwapStorage) -> Result<T, BindingError> + Send + 'static,
    {
        let storage = self.storage.clone();
        tokio::task::spawn_blocking(move || operation(storage.as_ref()))
            .await
            .map_err(|error| BoltzError::Store(format!("storage callback panicked: {error}")))?
            .map_err(|error| BoltzError::Store(error.to_string()))
    }
}

#[async_trait]
impl BoltzStorage for ForeignStorageAdapter {
    async fn upsert_swap(&self, swap: &BoltzSwap) -> Result<(), BoltzError> {
        let json = serde_json::to_string(swap).map_err(store_error)?;
        let id = swap.id.clone();
        let status = status_label(&swap.status).to_string();
        let terminal = swap.status.is_terminal();
        self.dispatch(move |storage| storage.upsert_swap(id, json, status, terminal))
            .await
    }

    async fn get_swap(&self, id: &str) -> Result<Option<BoltzSwap>, BoltzError> {
        let id = id.to_string();
        let json = self.dispatch(move |storage| storage.get_swap(id)).await?;
        json.map(|json| serde_json::from_str(&json).map_err(store_error))
            .transpose()
    }

    async fn list_active_swaps(&self) -> Result<Vec<BoltzSwap>, BoltzError> {
        let rows = self.dispatch(|storage| storage.list_active_swaps()).await?;
        let mut swaps = Vec::with_capacity(rows.len());
        for json in rows {
            let swap: BoltzSwap = serde_json::from_str(&json).map_err(store_error)?;
            // The foreign side filters on its is_terminal column; re-filter on
            // the deserialized state so a stale column can't resurrect a
            // finished swap.
            if !swap.status.is_terminal() {
                swaps.push(swap);
            }
        }
        Ok(swaps)
    }
}

#[async_trait]
impl DerivedKeyStore for ForeignStorageAdapter {
    async fn increment_key_index(&self) -> Result<u32, BoltzError> {
        self.dispatch(|storage| storage.next_key_index()).await
    }
}

fn store_error(error: impl std::fmt::Display) -> BoltzError {
    BoltzError::Store(error.to_string())
}

#[cfg(test)]
mod tests {
    use std::collections::HashMap;
    use std::sync::Mutex;
    use std::sync::atomic::{AtomicU32, Ordering};

    use boltz_client::{Asset, BridgeKind, SwapKeySource};

    use super::*;

    #[derive(Default)]
    struct MemoryStorage {
        swaps: Mutex<HashMap<String, StoredSwap>>,
        next_index: AtomicU32,
    }

    struct StoredSwap {
        json: String,
        status: String,
        is_terminal: bool,
    }

    impl SwapStorage for MemoryStorage {
        fn upsert_swap(
            &self,
            swap_id: String,
            swap_json: String,
            status: String,
            is_terminal: bool,
        ) -> Result<(), BindingError> {
            self.swaps.lock().unwrap().insert(
                swap_id,
                StoredSwap {
                    json: swap_json,
                    status,
                    is_terminal,
                },
            );
            Ok(())
        }

        fn get_swap(&self, swap_id: String) -> Result<Option<String>, BindingError> {
            Ok(self
                .swaps
                .lock()
                .unwrap()
                .get(&swap_id)
                .map(|stored| stored.json.clone()))
        }

        fn list_active_swaps(&self) -> Result<Vec<String>, BindingError> {
            Ok(self
                .swaps
                .lock()
                .unwrap()
                .values()
                .filter(|stored| !stored.is_terminal)
                .map(|stored| stored.json.clone())
                .collect())
        }

        fn next_key_index(&self) -> Result<u32, BindingError> {
            Ok(self.next_index.fetch_add(1, Ordering::SeqCst))
        }
    }

    /// Returns every stored row regardless of the terminal flag, so tests can
    /// prove the rust side re-filters after deserializing.
    struct UnfilteredStorage(MemoryStorage);

    impl SwapStorage for UnfilteredStorage {
        fn upsert_swap(
            &self,
            swap_id: String,
            swap_json: String,
            status: String,
            is_terminal: bool,
        ) -> Result<(), BindingError> {
            self.0.upsert_swap(swap_id, swap_json, status, is_terminal)
        }

        fn get_swap(&self, swap_id: String) -> Result<Option<String>, BindingError> {
            self.0.get_swap(swap_id)
        }

        fn list_active_swaps(&self) -> Result<Vec<String>, BindingError> {
            Ok(self
                .0
                .swaps
                .lock()
                .unwrap()
                .values()
                .map(|stored| stored.json.clone())
                .collect())
        }

        fn next_key_index(&self) -> Result<u32, BindingError> {
            self.0.next_key_index()
        }
    }

    struct FailingStorage;

    impl SwapStorage for FailingStorage {
        fn upsert_swap(
            &self,
            _swap_id: String,
            _swap_json: String,
            _status: String,
            _is_terminal: bool,
        ) -> Result<(), BindingError> {
            Err(BindingError::operation("storage", "boom"))
        }

        fn get_swap(&self, _swap_id: String) -> Result<Option<String>, BindingError> {
            Err(BindingError::operation("storage", "boom"))
        }

        fn list_active_swaps(&self) -> Result<Vec<String>, BindingError> {
            Err(BindingError::operation("storage", "boom"))
        }

        fn next_key_index(&self) -> Result<u32, BindingError> {
            Err(BindingError::operation("storage", "boom"))
        }
    }

    fn sample_swap(id: &str, status: BoltzSwapStatus) -> BoltzSwap {
        BoltzSwap {
            id: id.to_string(),
            status,
            bridge_kind: BridgeKind::Oft,
            key_source: SwapKeySource::Derived { claim_key_index: 0 },
            chain_id: 42_161,
            claim_address: "0x0000000000000000000000000000000000000001".to_string(),
            destination_address: "0x0000000000000000000000000000000000000002".to_string(),
            destination_chain: "Ethereum".to_string(),
            asset: Asset::Usdt0,
            refund_address: "0x0000000000000000000000000000000000000003".to_string(),
            erc20swap_address: "0x0000000000000000000000000000000000000004".to_string(),
            router_address: "0x0000000000000000000000000000000000000005".to_string(),
            invoice: "lnbc-test".to_string(),
            invoice_amount_sats: 10_000,
            onchain_amount: 9_950,
            expected_output_amount: 7_000_000,
            slippage_bps: 100,
            timeout_block_height: 1_000_000,
            lockup_tx_id: None,
            claim_tx_hash: None,
            pending_call_id: None,
            delivered_amount: None,
            bridge_ref: None,
            created_at: 1_700_000_000,
            updated_at: 1_700_000_000,
        }
    }

    #[tokio::test]
    async fn swaps_round_trip_through_the_foreign_trait() {
        let storage = Arc::new(MemoryStorage::default());
        let adapter = ForeignStorageAdapter::new(storage.clone());
        let failed = sample_swap(
            "failed",
            BoltzSwapStatus::Failed {
                reason: "test".to_string(),
            },
        );

        adapter
            .upsert_swap(&sample_swap("active", BoltzSwapStatus::Created))
            .await
            .unwrap();
        adapter.upsert_swap(&failed).await.unwrap();

        let roundtripped = adapter.get_swap("active").await.unwrap().unwrap();
        assert_eq!(roundtripped.id, "active");
        assert_eq!(roundtripped.invoice_amount_sats, 10_000);
        assert!(adapter.get_swap("missing").await.unwrap().is_none());

        // Convenience columns arrive without C# parsing the json.
        let stored = storage.swaps.lock().unwrap();
        assert_eq!(stored["active"].status, "created");
        assert!(!stored["active"].is_terminal);
        assert_eq!(stored["failed"].status, "failed");
        assert!(stored["failed"].is_terminal);
    }

    #[tokio::test]
    async fn upsert_overwrites_existing_rows() {
        let adapter = ForeignStorageAdapter::new(Arc::new(MemoryStorage::default()));
        let mut swap = sample_swap("swap", BoltzSwapStatus::Created);
        adapter.upsert_swap(&swap).await.unwrap();
        swap.status = BoltzSwapStatus::Completed;
        adapter.upsert_swap(&swap).await.unwrap();

        assert!(matches!(
            adapter.get_swap("swap").await.unwrap().unwrap().status,
            BoltzSwapStatus::Completed
        ));
    }

    #[tokio::test]
    async fn list_active_swaps_refilters_terminal_rows() {
        let adapter = ForeignStorageAdapter::new(Arc::new(MemoryStorage::default()));
        adapter
            .upsert_swap(&sample_swap("active", BoltzSwapStatus::Created))
            .await
            .unwrap();
        adapter
            .upsert_swap(&sample_swap("completed", BoltzSwapStatus::Completed))
            .await
            .unwrap();
        let active = adapter.list_active_swaps().await.unwrap();
        assert_eq!(active.len(), 1);
        assert_eq!(active[0].id, "active");

        // Even a foreign side that ignores its is_terminal column cannot
        // resurrect finished swaps: rust re-filters the deserialized state.
        let unfiltered =
            ForeignStorageAdapter::new(Arc::new(UnfilteredStorage(MemoryStorage::default())));
        unfiltered
            .upsert_swap(&sample_swap("active", BoltzSwapStatus::Created))
            .await
            .unwrap();
        unfiltered
            .upsert_swap(&sample_swap("completed", BoltzSwapStatus::Completed))
            .await
            .unwrap();
        let active = unfiltered.list_active_swaps().await.unwrap();
        assert_eq!(active.len(), 1);
        assert_eq!(active[0].id, "active");
    }

    #[tokio::test]
    async fn key_index_passes_through_monotonically() {
        let adapter = ForeignStorageAdapter::new(Arc::new(MemoryStorage::default()));
        assert_eq!(adapter.increment_key_index().await.unwrap(), 0);
        assert_eq!(adapter.increment_key_index().await.unwrap(), 1);
        assert_eq!(adapter.increment_key_index().await.unwrap(), 2);
    }

    #[tokio::test]
    async fn storage_errors_surface_as_store_errors() {
        let adapter = ForeignStorageAdapter::new(Arc::new(FailingStorage));
        let swap = sample_swap("swap", BoltzSwapStatus::Created);

        for error in [
            adapter.upsert_swap(&swap).await.err().unwrap(),
            adapter.get_swap("swap").await.err().unwrap(),
            adapter.list_active_swaps().await.err().unwrap(),
            adapter.increment_key_index().await.err().unwrap(),
        ] {
            assert!(matches!(&error, BoltzError::Store(message) if message.contains("boom")));
        }
    }
}
