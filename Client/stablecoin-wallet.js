import { createAppKit } from '@reown/appkit'
import { EthersAdapter } from '@reown/appkit-adapter-ethers'
import { SolanaAdapter } from '@reown/appkit-adapter-solana'
import {
  arbitrum,
  avalanche,
  base,
  berachain,
  codex,
  hedera,
  ink,
  linea,
  mainnet,
  monad,
  optimism,
  plumeMainnet,
  polygon,
  sei,
  solana,
  sonic,
  unichain,
  worldchain,
  xdc
} from '@reown/appkit/networks'

const containers = [...document.querySelectorAll('[data-stablecoin-wallet]')]
if (containers.length > 0) {

  const projectId = document.querySelector('script[data-reown-project-id]')?.dataset.reownProjectId ||
    'ba2fd40da144b7017436e42851ec62ae'
  const walletNetworkById = {
    arbitrum,
    avalanche,
    base,
    berachain,
    codex,
    ethereum: mainnet,
    hedera,
    ink,
    linea,
    monad,
    optimism,
    plume: plumeMainnet,
    polygon,
    sei,
    solana,
    sonic,
    unichain,
    worldchain,
    xdc
  }
  const networks = Object.values(walletNetworkById)
  function caipNetworkId(network) {
    return network.caipNetworkId ?? `${network.chainNamespace ?? 'eip155'}:${network.id}`
  }
  const networkIdByCaipNetworkId = Object.fromEntries(
    Object.entries(walletNetworkById).map(([networkId, network]) => [
      caipNetworkId(network),
      networkId
    ])
  )

  // TODO: Add a Tron adapter and destination mapping when Tron settlement is
  // end-to-end tested.
  const namespaceByNetworkId = Object.fromEntries(
    Object.entries(walletNetworkById).map(([networkId, network]) => [
      networkId,
      network.chainNamespace ?? (network === solana ? 'solana' : 'eip155')
    ])
  )

  const modal = createAppKit({
    adapters: [new EthersAdapter(), new SolanaAdapter()],
    networks,
    projectId,
    metadata: {
      name: 'BTCPay Server Arkade',
      description: 'Choose a stablecoin settlement wallet for Arkade',
      url: window.location.origin,
      icons: []
    },
    features: {
      analytics: false,
      email: false,
      socials: false,
      swaps: false,
      onramp: false,
      receive: false,
      send: false,
      history: false,
      pay: false
    }
  })

  let activeTarget = null
  let syncingChain = false
  let syncingAddress = false
  let connectingFromBlank = false
  let closeModalTimer = null
  const programmaticNetworkSwitches = new Set()

  function setStatus(container, message, isError = false) {
    const status = container.querySelector('[data-wallet-connect-status]')
    status.textContent = message
    status.classList.toggle('text-danger', isError)
    status.classList.toggle('text-secondary', !isError)
  }

  function accountAddress(state) {
    if (typeof state === 'string') return state
    return state?.address ?? null
  }

  function syncSelectedChain(target, chain) {
    if (target.chain.value === chain) return

    syncingChain = true
    try {
      target.chain.value = chain
      target.chain.dispatchEvent(new Event('change', { bubbles: true }))
    } finally {
      syncingChain = false
    }
  }

  function syncAddress(address, value) {
    syncingAddress = true
    try {
      address.value = value
      address.dispatchEvent(new Event('input', { bubbles: true }))
    } finally {
      syncingAddress = false
    }
  }

  function closeModalAfterNetworkChange() {
    window.clearTimeout(closeModalTimer)
    closeModalTimer = window.setTimeout(() => {
      void modal.close().catch(error => console.error('Unable to close Reown AppKit', error))
    }, 0)
  }

  function syncActiveConnection() {
    if (!activeTarget) return

    const namespace = modal.getActiveChainNamespace()
    const network = modal.getCaipNetwork()
    const connectedNetworkId = networkIdByCaipNetworkId[network?.caipNetworkId]
    if (!namespace || !connectedNetworkId) {
      setStatus(activeTarget.container, 'Choose a supported settlement network in your wallet.', true)
      return
    }

    const connectedOption = [...activeTarget.chain.options]
      .find(option => option.dataset.stablecoinNetwork === connectedNetworkId)
    const connectedNetworkName = connectedOption?.value ?? connectedNetworkId
    const supportedAssets = connectedOption?.dataset.stablecoinAssets?.split(' ') ?? []
    if (!supportedAssets.includes(activeTarget.asset.value)) {
      syncAddress(activeTarget.address, '')
      setStatus(
        activeTarget.container,
        `${connectedNetworkName} is not available for ${activeTarget.asset.value} settlement.`,
        true
      )
      return
    }

    syncSelectedChain(activeTarget, connectedNetworkName)
    const address = accountAddress(modal.getAccount(namespace))
    if (!address) {
      syncAddress(activeTarget.address, '')
      setStatus(activeTarget.container, `Connect a ${connectedNetworkName} wallet to continue.`)
    } else {
      syncAddress(activeTarget.address, address)
      setStatus(activeTarget.container, `${connectedNetworkName} wallet address selected.`)
    }
  }

  function handleConnectionChange() {
    if (!connectingFromBlank) syncActiveConnection()
  }

  function handleAppKitEvent(state) {
    const event = state.data
    if (event?.event === 'CONNECT_SUCCESS' && connectingFromBlank) {
      connectingFromBlank = false
      syncActiveConnection()
      closeModalAfterNetworkChange()
      return
    }

    if (event?.event !== 'SWITCH_NETWORK') return

    const networkId = event.properties?.network
    if (programmaticNetworkSwitches.delete(networkId)) return
    if (!activeTarget || !modal.getState().open) return

    connectingFromBlank = false
    syncActiveConnection()
    closeModalAfterNetworkChange()
  }

  async function switchNetwork(network) {
    const networkId = caipNetworkId(network)
    programmaticNetworkSwitches.add(networkId)
    try {
      await modal.switchNetwork(network)
    } catch (error) {
      programmaticNetworkSwitches.delete(networkId)
      throw error
    }
  }

  modal.subscribeAccount(handleConnectionChange)
  modal.subscribeNetwork(handleConnectionChange)
  modal.subscribeEvents(handleAppKitEvent)

  containers.forEach(container => {
    const asset = container.querySelector('[data-stablecoin-asset]')
    const chain = container.querySelector('[data-stablecoin-chain]')
    const address = container.querySelector('[data-stablecoin-address]')
    const button = container.querySelector('[data-wallet-connect]')

    function filterNetworksForAsset() {
      const selectedChain = chain.value
      let selectedChainSupported = selectedChain === ''

      for (const option of chain.querySelectorAll('option[data-stablecoin-assets]')) {
        const supported = option.dataset.stablecoinAssets.split(' ').includes(asset.value)
        option.hidden = !supported
        option.disabled = !supported
        if (option.value === selectedChain) selectedChainSupported = supported
      }

      if (selectedChainSupported) return

      chain.value = ''
      syncAddress(address, '')
      if (activeTarget?.container === container) activeTarget = null
      connectingFromBlank = false
      setStatus(container, `Select a network that supports ${asset.value}.`)
    }

    asset.addEventListener('change', filterNetworksForAsset)
    filterNetworksForAsset()

    address.addEventListener('input', () => {
      if (!syncingAddress && activeTarget?.container === container) {
        activeTarget = null
        connectingFromBlank = false
      }
    })

    chain.addEventListener('change', async () => {
      if (syncingChain) return

      syncAddress(address, '')
      if (activeTarget?.container === container) activeTarget = null
      connectingFromBlank = false

      const selectedNetworkId = chain.selectedOptions[0]?.dataset.stablecoinNetwork
      const network = walletNetworkById[selectedNetworkId]
      if (!network) {
        setStatus(container, 'Select a network or choose one through Connect wallet.')
        return
      }

      setStatus(container, `Switching wallet connection to ${chain.value}…`)
      try {
        await switchNetwork(network)
        setStatus(container, `${chain.value} selected. Connect a wallet or enter a new address.`)
      } catch (error) {
        console.error('Unable to switch the AppKit network', error)
        setStatus(container, `Your wallet did not switch to ${chain.value}.`, true)
      }
    })

    button.addEventListener('click', async () => {
      const selectedNetworkName = chain.value
      const selectedNetworkId = chain.selectedOptions[0]?.dataset.stablecoinNetwork
      const network = walletNetworkById[selectedNetworkId]
      const namespace = namespaceByNetworkId[selectedNetworkId]
      if (!network || !namespace) {
        activeTarget = { container, asset, chain, address }
        connectingFromBlank = true
        setStatus(container, 'Choose a wallet in Reown…')
        try {
          await modal.open()
        } catch (error) {
          connectingFromBlank = false
          console.error('Unable to open Reown AppKit', error)
          setStatus(container, 'Unable to open the wallet connector. Try again.', true)
        }
        return
      }

      activeTarget = { container, asset, chain, address }
      setStatus(container, `Opening ${selectedNetworkName} wallets…`)

      try {
        await switchNetwork(network)
        syncActiveConnection()
        await modal.open({ namespace })
      } catch (error) {
        console.error('Unable to open Reown AppKit', error)
        setStatus(container, 'Unable to open the wallet connector. Try again.', true)
      }
    })
  })
}
