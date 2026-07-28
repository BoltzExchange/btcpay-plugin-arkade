import { createAppKit } from '@reown/appkit'
import { EthersAdapter } from '@reown/appkit-adapter-ethers'
import { SolanaAdapter } from '@reown/appkit-adapter-solana'
import { arbitrum, mainnet, polygon, solana } from '@reown/appkit/networks'

const containers = [...document.querySelectorAll('[data-stablecoin-wallet]')]
if (containers.length > 0) {

  const projectId = document.querySelector('script[data-reown-project-id]')?.dataset.reownProjectId ||
    'ba2fd40da144b7017436e42851ec62ae'
  const networks = [arbitrum, mainnet, polygon, solana]
  const networkByChain = {
    'Arbitrum One': arbitrum,
    Ethereum: mainnet,
    'Polygon PoS': polygon,
    Solana: solana
  }
  function caipNetworkId(network) {
    return network.caipNetworkId ?? `${network.chainNamespace ?? 'eip155'}:${network.id}`
  }
  const chainByCaipNetworkId = Object.fromEntries(
    Object.entries(networkByChain).map(([chain, network]) => [caipNetworkId(network), chain])
  )

  // TODO: Add a TRON adapter and destination mapping when TRON settlement ships.
  const namespaceByChain = {
    'Arbitrum One': 'eip155',
    Ethereum: 'eip155',
    'Polygon PoS': 'eip155',
    Solana: 'solana'
  }

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
    const connectedChain = chainByCaipNetworkId[network?.caipNetworkId]
    if (!namespace || !connectedChain) {
      setStatus(activeTarget.container, 'Choose Arbitrum, Ethereum, Polygon, or Solana in your wallet.', true)
      return
    }

    syncSelectedChain(activeTarget, connectedChain)
    const address = accountAddress(modal.getAccount(namespace))
    if (!address) {
      syncAddress(activeTarget.address, '')
      setStatus(activeTarget.container, `Connect a ${connectedChain} wallet to continue.`)
    } else {
      syncAddress(activeTarget.address, address)
      setStatus(activeTarget.container, `${connectedChain} wallet address selected.`)
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
    const chain = container.querySelector('[data-stablecoin-chain]')
    const address = container.querySelector('[data-stablecoin-address]')
    const button = container.querySelector('[data-wallet-connect]')

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

      const network = networkByChain[chain.value]
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
      const selectedChain = chain.value
      const network = networkByChain[selectedChain]
      const namespace = namespaceByChain[selectedChain]
      if (!network || !namespace) {
        activeTarget = { container, chain, address }
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

      activeTarget = { container, chain, address }
      setStatus(container, `Opening ${selectedChain} wallets…`)

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
