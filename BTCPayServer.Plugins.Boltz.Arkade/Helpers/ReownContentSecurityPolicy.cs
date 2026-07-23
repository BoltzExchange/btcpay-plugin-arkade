using BTCPayServer.Security;

namespace BTCPayServer.Plugins.Boltz.Arkade.Helpers;

public static class ReownContentSecurityPolicy
{
    private static readonly (string Directive, string[] Sources)[] Directives =
    [
        ("connect-src",
        [
            "'self'",
            "https://rpc.walletconnect.com",
            "https://rpc.walletconnect.org",
            "https://relay.walletconnect.com",
            "https://relay.walletconnect.org",
            "wss://relay.walletconnect.com",
            "wss://relay.walletconnect.org",
            "https://pulse.walletconnect.com",
            "https://pulse.walletconnect.org",
            "https://api.web3modal.com",
            "https://api.web3modal.org",
            "https://keys.walletconnect.com",
            "https://keys.walletconnect.org",
            "https://notify.walletconnect.com",
            "https://notify.walletconnect.org",
            "https://echo.walletconnect.com",
            "https://echo.walletconnect.org",
            "https://push.walletconnect.com",
            "https://push.walletconnect.org",
            "wss://www.walletlink.org",
            "https://cca-lite.coinbase.com",
            "https://arb1.arbitrum.io",
            "https://ethereum.reth.rs",
            "https://polygon.drpc.org"
        ]),
        ("frame-src",
        [
            "'self'",
            "https://verify.walletconnect.com",
            "https://verify.walletconnect.org",
            "https://secure.walletconnect.com",
            "https://secure.walletconnect.org"
        ]),
        ("img-src",
        [
            "'self'", "data:", "blob:",
            "https://walletconnect.org", "https://walletconnect.com",
            "https://secure.walletconnect.com", "https://secure.walletconnect.org",
            "https://tokens-data.1inch.io", "https://tokens.1inch.io",
            "https://ipfs.io", "https://cdn.zerion.io"
        ]),
        ("font-src",
        [
            "'self'", "data:", "https://fonts.gstatic.com", "https://fonts.reown.com"
        ])
    ];

    public static void Configure(ContentSecurityPolicies csp)
    {
        foreach (var (directive, sources) in Directives)
            foreach (var source in sources)
                csp.Add(directive, source);
    }
}
