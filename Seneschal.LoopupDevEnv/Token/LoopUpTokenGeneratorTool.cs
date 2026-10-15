using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Seneschal.Sdk;

namespace DevEnvironment.Token;

// Signs in to a LoopUp environment the way OneUI does and returns the bearer token, on a page and for the AI assistant
public sealed class LoopUpTokenGeneratorTool : IToolModule, IToolLifecycle, IToolMcp
{
    public ToolMetadata Metadata { get; } = new(
        Id: "loopup-token-generator",
        Title: "LoopUp Token Generator",
        Description: "Get a bearer token for an environment",
        Group: "LoopUp",
        Icon: Icons.Material.Filled.VpnKey,
        ComponentType: typeof(LoopUpTokenGeneratorView),
        SearchTerms: ["token", "bearer", "jwt", "login", "auth", "authorization", "identity", "oidc", "access token", "loopup"]);

    public void ConfigureServices(IServiceCollection services, IExtensionContext context)
    {
        services.AddSingleton<TokenClient>();
        // The extension's own folder: the environment and emails it remembers go there, never a password
        services.AddSingleton(new TokenSettings(context.DataDirectory));
    }

    public IEnumerable<McpTool> McpTools(IServiceProvider services)
    {
        var client = services.GetRequiredService<TokenClient>();

        yield return new McpTool("loopup_token_generator_environments", "List the LoopUp environments a bearer token can be requested from",
            McpEffect.Read, McpSchema.None, _ => TokenEnvironment.All.Select(e => new { e.Id, e.Name, e.Authority, e.AppUrl }));

        yield return new McpTool("loopup_token_generator_get", "Sign in to a LoopUp environment the way OneUI does and return the bearer token (an access token for the Authorization header)",
            McpEffect.Read, McpSchema.Object().Choice("environment", "Which environment (default local)", TokenEnvironment.All.Select(e => e.Id))
                .Text("email", "The login email", required: true).Text("password", "The login password", required: true),
            async (args, cancel) =>
            {
                var environment = TokenEnvironment.Find(args.Optional("environment"));
                try
                {
                    var token = await client.GetAsync(environment, args.Text("email"), args.Text("password"), cancel);
                    return new { Environment = environment.Name, token.AccessToken, Header = token.AuthorizationHeader, ExpiresAt = token.ExpiresAt, token.Scope };
                }
                catch (TokenException ex)
                {
                    throw new McpException(ex.Message);
                }
            });
    }

    public void Started(IServiceProvider services, IExtensionContext context)
    {
    }
}
