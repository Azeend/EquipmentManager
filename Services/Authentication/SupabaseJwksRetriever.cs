using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace EquipmentManager.Services.Authentication;

/// <summary>Загружает только совместимые публичные ключи подписей из доверенного JWKS.</summary>
public sealed class SupabaseJwksRetriever(string algorithm) : IConfigurationRetriever<OpenIdConnectConfiguration>
{
    /// <inheritdoc />
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(string address, IDocumentRetriever retriever,
        CancellationToken cancellationToken)
    {
        var json = await retriever.GetDocumentAsync(address, cancellationToken);
        var allowed = new JsonWebKeySet();
        foreach (var key in new JsonWebKeySet(json).Keys)
        {
            if ((!string.IsNullOrEmpty(key.Use) && key.Use != "sig")
                || (!string.IsNullOrEmpty(key.Alg) && key.Alg != algorithm)
                || (key.KeyOps.Count > 0 && !key.KeyOps.Contains("verify"))
                || (algorithm == "ES256" && (key.Kty != "EC" || key.Crv != "P-256"))
                || (algorithm == "RS256" && key.Kty != "RSA")
                || algorithm is not ("ES256" or "RS256"))
            {
                continue;
            }
            allowed.Keys.Add(key);
        }
        var configuration = new OpenIdConnectConfiguration();
        foreach (var key in allowed.GetSigningKeys()) { configuration.SigningKeys.Add(key); }
        if (configuration.SigningKeys.Count == 0)
        {
            throw new InvalidOperationException("JWKS не содержит совместимых публичных ключей подписи.");
        }
        return configuration;
    }
}
