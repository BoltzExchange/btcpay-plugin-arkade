using BTCPayServer.Security;

namespace BTCPayServer.Plugins.Boltz.Arkade.Helpers;

public static class ReownContentSecurityPolicy
{
    private static readonly (string Directive, string[] Sources)[] Directives =
    [
        ("connect-src",
        [
            "'self'",
            "https://api.web3modal.org",
            "https://pulse.walletconnect.org",
            "https://rpc.walletconnect.org",
            "wss://relay.walletconnect.org",
            "https://www.walletlink.org",
            "wss://www.walletlink.org",
            "https://arb1.arbitrum.io",
            "https://ethereum.reth.rs",
            "https://polygon.drpc.org"
        ]),
        ("frame-src",
        [
            "'self'",
            "https://verify.walletconnect.org"
        ]),
        ("img-src",
        [
            "'self'", "data:", "blob:"
        ]),
        ("font-src",
        [
            "'self'", "https://fonts.reown.com"
        ])
    ];

    public static void Configure(ContentSecurityPolicies csp)
    {
        foreach (var (directive, sources) in Directives)
            foreach (var source in sources)
                csp.Add(directive, source);
    }
}
