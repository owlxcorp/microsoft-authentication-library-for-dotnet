// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.AppConfig;
using Microsoft.Identity.Client.AuthScheme.PoP;
using Microsoft.Identity.Client.Extensibility;
#if NET_CORE
using Microsoft.Identity.Client.Broker;
#endif
using Microsoft.Identity.Client.Utils;
using Microsoft.Identity.Test.Common;
using Microsoft.Identity.Test.Integration.Infrastructure;
using Microsoft.Identity.Test.Integration.net45.Infrastructure;
using Microsoft.Identity.Test.LabInfrastructure;
using Microsoft.Identity.Test.Unit;
using Microsoft.IdentityModel.Protocols.SignedHttpRequest;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Identity.Test.Common.Core.Helpers;
using Microsoft.IdentityModel.Abstractions;
using Microsoft.Identity.Client.TelemetryCore.TelemetryClient;
using Microsoft.Identity.Client.AuthScheme;
using Microsoft.Identity.Client.TelemetryCore;
using Microsoft.Identity.Client.Internal;
using System.Security.Claims;

namespace Microsoft.Identity.Test.Integration.HeadlessTests
{

    // Note: these tests require permission to a KeyVault Microsoft account;
    // Please ignore them if you are not a Microsoft FTE, they will run as part of the CI build
    [TestClass]    
    public class PoPTests
    {
        // This endpoint is hosted in the MSID Lab and is able to verify any pop token bound to an HTTP request
        private const string PoPValidatorEndpoint = "https://signedhttprequest.azurewebsites.net/api/validateSHR";

        private static readonly string[] s_keyvaultScope = { "https://vault.azure.net/.default" };

        private const string PublicCloudConfidentialClientID = "16dab2ba-145d-4b1b-8569-bf4b9aed4dc8";
        private const string PublicCloudTestAuthority = "https://login.windows.net/72f988bf-86f1-41af-91ab-2d7cd011db47";
        private const string ProtectedUrl = "https://www.contoso.com/path1/path2?queryParam1=a&queryParam2=b";
        private static string s_publicCloudCcaSecret;
        private KeyVaultSecretsProvider _keyVault;

        private string _popValidationEndpointSecret;

        [TestInitialize]
        public void TestInitialize()
        {
            TestCommon.ResetInternalStaticCaches();
            if (_popValidationEndpointSecret == null)
            {
                _popValidationEndpointSecret = LabUserHelper.KeyVaultSecretsProviderMsal.GetSecretByName("automation-pop-validation-endpoint", "841fc7c2ccdd48d7a9ef727e4ae84325").Value;
            }

            if (_keyVault == null)
            {
                _keyVault = new KeyVaultSecretsProvider(KeyVaultInstance.MsalTeam);
                s_publicCloudCcaSecret = _keyVault.GetSecretByName(TestConstants.MsalCCAKeyVaultSecretName).Value;
            }
        }

        [TestMethod]
        public async Task PoP_MultipleKeys_Async()
        {
            await MultipleKeys_Async().ConfigureAwait(false);
        }

        [TestMethod]
        public async Task PoP_BearerAndPoP_CanCoexist_Async()
        {
            var labResponse = await LabUserHelper.GetDefaultUserAsync().ConfigureAwait(false);
            await BearerAndPoP_CanCoexist_Async().ConfigureAwait(false);
        }

        [TestMethod]
        public async Task HappyPath_Async()
        {
            await RunTestWithClientSecretAsync(PublicCloudConfidentialClientID, PublicCloudTestAuthority, s_publicCloudCcaSecret).ConfigureAwait(false);
        }

        private async Task BearerAndPoP_CanCoexist_Async()
        {
            // Arrange
            var popConfig = new PoPAuthenticationConfiguration(new Uri(ProtectedUrl));
            popConfig.HttpMethod = HttpMethod.Get;

            var cca = ConfidentialClientApplicationBuilder
                .Create(PublicCloudConfidentialClientID)
                .WithExperimentalFeatures()
                .WithClientSecret(s_publicCloudCcaSecret)
                .WithTestLogging()
                .WithAuthority(PublicCloudTestAuthority).Build();
            ConfigureInMemoryCache(cca);

            // Act - acquire both a PoP and a Bearer token
            Trace.WriteLine("Getting a PoP token");
            AuthenticationResult result = await cca
                .AcquireTokenForClient(s_keyvaultScope)
                .WithProofOfPossession(popConfig)
                .ExecuteAsync()
                .ConfigureAwait(false);

            Assert.AreEqual("pop", result.TokenType);
            await VerifyPoPTokenAsync(
                                      PublicCloudConfidentialClientID,
                                       ProtectedUrl,
                                       HttpMethod.Get,
                                       result).ConfigureAwait(false);

            Trace.WriteLine("Getting a Bearer token");
            result = await cca
                .AcquireTokenForClient(s_keyvaultScope)
                .ExecuteAsync()
                .ConfigureAwait(false);
            Assert.AreEqual("Bearer", result.TokenType);
            Assert.AreEqual(
                2,
                (cca as ConfidentialClientApplication).AppTokenCacheInternal.Accessor.GetAllAccessTokens().Count());
        }

        private async Task MultipleKeys_Async()
        {

            var cryptoProvider = new RSACertificatePopCryptoProvider(GetCertificate());

            var popConfig1 = new PoPAuthenticationConfiguration(new Uri(ProtectedUrl));
            popConfig1.HttpMethod = HttpMethod.Get;
            popConfig1.PopCryptoProvider = cryptoProvider;
            const string OtherProtectedUrl = "https://www.bing.com/path3/path4?queryParam5=c&queryParam6=d";
            var popConfig2 = new PoPAuthenticationConfiguration(new Uri(OtherProtectedUrl));
            popConfig2.HttpMethod = HttpMethod.Post;
            popConfig2.PopCryptoProvider = cryptoProvider;

            var cca = ConfidentialClientApplicationBuilder.Create(PublicCloudConfidentialClientID)
                .WithExperimentalFeatures()
                .WithTestLogging()
                .WithAuthority(PublicCloudTestAuthority)
                .WithClientSecret(s_publicCloudCcaSecret).Build();
            ConfigureInMemoryCache(cca);

            var result = await cca
                .AcquireTokenForClient(s_keyvaultScope)
                .WithProofOfPossession(popConfig1)
                .ExecuteAsync(CancellationToken.None)
                .ConfigureAwait(false);

            Assert.AreEqual("pop", result.TokenType);
            await VerifyPoPTokenAsync(
                           PublicCloudConfidentialClientID,
                            ProtectedUrl,
                            HttpMethod.Get,
                            result).ConfigureAwait(false);

            // recreate the pca to ensure that the silent call is served from the cache, i.e. the key remains stable
            cca = ConfidentialClientApplicationBuilder
                .Create(PublicCloudConfidentialClientID)
                .WithExperimentalFeatures()
                .WithAuthority(PublicCloudTestAuthority)
                .WithClientSecret(s_publicCloudCcaSecret)
                .WithHttpClientFactory(new NoAccessHttpClientFactory()) // token should be served from the cache, no network access necessary
                .Build();
            ConfigureInMemoryCache(cca);

            result = await cca
                .AcquireTokenForClient(s_keyvaultScope)
                .WithProofOfPossession(popConfig1)
                .ExecuteAsync()
                .ConfigureAwait(false);

            Assert.AreEqual(TokenSource.Cache, result.AuthenticationResultMetadata.TokenSource);
            Assert.AreEqual("pop", result.TokenType);

            await VerifyPoPTokenAsync(
                           PublicCloudConfidentialClientID,
                            ProtectedUrl,
                            HttpMethod.Get,
                            result).ConfigureAwait(false);

            // Call some other Uri - the same pop assertion can be reused, i.e. no need to call Evo
            result = await cca
              .AcquireTokenForClient(s_keyvaultScope)
              .WithProofOfPossession(popConfig2)
              .ExecuteAsync()
              .ConfigureAwait(false);

            Assert.AreEqual("pop", result.TokenType);
            Assert.AreEqual(TokenSource.Cache, result.AuthenticationResultMetadata.TokenSource);

            await VerifyPoPTokenAsync(
                            PublicCloudConfidentialClientID,
                             OtherProtectedUrl,
                             HttpMethod.Post,
                             result).ConfigureAwait(false);
        }

        public async Task RunTestWithClientSecretAsync(string clientID, string authority, string secret)
        {
            var popConfig = new PoPAuthenticationConfiguration(new Uri(ProtectedUrl));
            popConfig.HttpMethod = HttpMethod.Get;

            var confidentialApp = ConfidentialClientApplicationBuilder
                .Create(clientID)
                .WithExperimentalFeatures()
                .WithAuthority(PublicCloudTestAuthority)
                .WithClientSecret(secret)
                .WithTestLogging()
                .Build();

            var result = await confidentialApp.AcquireTokenForClient(s_keyvaultScope)
                .WithProofOfPossession(popConfig)
                .ExecuteAsync(CancellationToken.None)
                .ConfigureAwait(false);

            Assert.AreEqual("pop", result.TokenType);
            await VerifyPoPTokenAsync(
                PublicCloudConfidentialClientID,
                 ProtectedUrl,
                 HttpMethod.Get,
                 result).ConfigureAwait(false);
        }

        [TestMethod]
        public async Task PopTestWithConfigObjectAsync()
        {
            var confidentialApp = ConfidentialClientApplicationBuilder
                .Create(PublicCloudConfidentialClientID)
                .WithExperimentalFeatures()
                .WithAuthority(PublicCloudTestAuthority)
                .WithClientSecret(s_publicCloudCcaSecret)
                .WithTestLogging()
                .Build();

            var popConfig = new PoPAuthenticationConfiguration(new Uri(ProtectedUrl));
            popConfig.PopCryptoProvider = new RSACertificatePopCryptoProvider(GetCertificate());
            popConfig.HttpMethod = HttpMethod.Get;

            var result = await confidentialApp.AcquireTokenForClient(s_keyvaultScope)
                .WithProofOfPossession(popConfig)
                .ExecuteAsync(CancellationToken.None)
                .ConfigureAwait(false);

            Assert.AreEqual("pop", result.TokenType);
            await VerifyPoPTokenAsync(
                PublicCloudConfidentialClientID,
                 ProtectedUrl,
                 HttpMethod.Get,
                 result).ConfigureAwait(false);
        }

        [TestMethod]
        public async Task PopTestWithRSAAsync()
        {
            var telemetryClient = new TestTelemetryClient(TestConstants.ClientId);
            var confidentialApp = ConfidentialClientApplicationBuilder
                .Create(PublicCloudConfidentialClientID)
                .WithExperimentalFeatures()
                .WithAuthority(PublicCloudTestAuthority)
                .WithClientSecret(s_publicCloudCcaSecret)
                .WithTelemetryClient(telemetryClient)
                .Build();

            //RSA provider
            var popConfig = new PoPAuthenticationConfiguration(new Uri(ProtectedUrl));
            popConfig.PopCryptoProvider = new RSACertificatePopCryptoProvider(GetCertificate());
            popConfig.HttpMethod = HttpMethod.Get;

            var result = await confidentialApp.AcquireTokenForClient(s_keyvaultScope)
                .WithProofOfPossession(popConfig)
                .ExecuteAsync(CancellationToken.None)
                .ConfigureAwait(false);

            Assert.AreEqual("pop", result.TokenType);
            await VerifyPoPTokenAsync(
                PublicCloudConfidentialClientID,
                 ProtectedUrl,
                 HttpMethod.Get,
                 result).ConfigureAwait(false);

            MsalTelemetryEventDetails eventDetails = telemetryClient.TestTelemetryEventDetails;
            Assert.IsNotNull(eventDetails);
            Assert.AreEqual(Convert.ToInt64(TokenType.Pop), eventDetails.Properties[TelemetryConstants.TokenType]);
        }

        [TestMethod]
        public async Task PopTest_ExternalWilsonSigning_Async()
        {
            var confidentialApp = ConfidentialClientApplicationBuilder
                .Create(PublicCloudConfidentialClientID)
                .WithExperimentalFeatures()
                .WithAuthority(PublicCloudTestAuthority)
                .WithClientSecret(s_publicCloudCcaSecret)
                .Build();

            // Create an RSA key Wilson style (SigningCredentials)
            var key = CreateRsaSecurityKey();
            var popCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);

            var popConfig = new PoPAuthenticationConfiguration()
            {
                PopCryptoProvider = new SigningCredentialsToPopCryptoProviderAdapter(popCredentials, true),
                SignHttpRequest = false,
            };

            var result = await confidentialApp.AcquireTokenForClient(s_keyvaultScope)
                .WithProofOfPossession(popConfig)
                .ExecuteAsync(CancellationToken.None)
                .ConfigureAwait(false);

            Assert.AreEqual("pop", result.TokenType);
            Assert.AreEqual(
                TokenSource.IdentityProvider,
                result.AuthenticationResultMetadata.TokenSource);

            SignedHttpRequestDescriptor signedHttpRequestDescriptor =
                new SignedHttpRequestDescriptor(
                    result.AccessToken,
                    new IdentityModel.Protocols.HttpRequestData()
                    {
                        Uri = new Uri(ProtectedUrl),
                        Method = HttpMethod.Post.ToString()
                    },
                    popCredentials);
            var signedHttpRequestHandler = new SignedHttpRequestHandler();
            string req = signedHttpRequestHandler.CreateSignedHttpRequest(signedHttpRequestDescriptor);

            await VerifyPoPTokenAsync(
                PublicCloudConfidentialClientID,
                 ProtectedUrl,
                 HttpMethod.Post,
                 req, "pop").ConfigureAwait(false);

            var result2 = await confidentialApp.AcquireTokenForClient(s_keyvaultScope)
             .WithProofOfPossession(popConfig)
             .ExecuteAsync(CancellationToken.None)
             .ConfigureAwait(false);
            Assert.AreEqual(
                TokenSource.Cache,
                result2.AuthenticationResultMetadata.TokenSource);
        }

        [TestMethod]
        public async Task PopTestWithECDAsync()
        {
            var confidentialApp = ConfidentialClientApplicationBuilder
                .Create(PublicCloudConfidentialClientID)
                .WithExperimentalFeatures()
                .WithAuthority(PublicCloudTestAuthority)
                .WithClientSecret(s_publicCloudCcaSecret)
                .Build();

            //ECD Provider
            var popConfig = new PoPAuthenticationConfiguration(new Uri(ProtectedUrl));
            popConfig.PopCryptoProvider = new ECDCertificatePopCryptoProvider();
            popConfig.HttpMethod = HttpMethod.Post;

            var result = await confidentialApp.AcquireTokenForClient(s_keyvaultScope)
                .WithProofOfPossession(popConfig)
                .ExecuteAsync(CancellationToken.None)
                .ConfigureAwait(false);

            Assert.AreEqual("pop", result.TokenType);
            await VerifyPoPTokenAsync(
                PublicCloudConfidentialClientID,
                ProtectedUrl,
                HttpMethod.Post,
                result).ConfigureAwait(false);
        }

        [TestMethod]
        public async Task NewPOP_WithKeyIdOnly_Async()
        {
            // Arrange - outside MSAL

            // 1.1. Create an RSA key (here using Wilson primitives, but vanialla crypto primitives also work, see ComputeCannonicalJwk bellow for example
            RsaSecurityKey popKey = CreateRsaSecurityKey();
            // 1.2. Get the JWK and base64 encode it
            string base64EncodedJwk = Base64UrlHelpers.Encode(popKey.ComputeJwkThumbprint());
            // 1.3. Put it in JSON format
            var reqCnf = $@"{{""kid"":""{base64EncodedJwk}""}}";
            // 1.4. Base64 encode it again
            var keyId = Base64UrlHelpers.Encode(reqCnf);

            // Arrange MSALfin

            // 2. Create a normal CCA 
            var confidentialApp = ConfidentialClientApplicationBuilder
                .Create(PublicCloudConfidentialClientID)
                .WithExperimentalFeatures()
                .WithAuthority(PublicCloudTestAuthority)
                .WithClientSecret(s_publicCloudCcaSecret)
                .Build();

            // 3. When acquiring a token, use WithPopKeyId and OnBeforeTokenRequest extensiblity methods
            var result = await confidentialApp.AcquireTokenForClient(s_keyvaultScope)
                 .WithProofOfPosessionKeyId(keyId, "pop")       // ensure tokens are bound to the key_id
                 .OnBeforeTokenRequest((data) =>
                 {
                     // add extra data to request
                     data.BodyParameters.Add("req_cnf", keyId);
                     data.BodyParameters.Add("token_type", "pop");

                     return Task.CompletedTask;
                 })
                .ExecuteAsync(CancellationToken.None)
                .ConfigureAwait(false);

            Assert.AreEqual("pop", result.TokenType);
            Assert.AreEqual(
                TokenSource.IdentityProvider,
                result.AuthenticationResultMetadata.TokenSource);

            // Outside MSAL - Create the SHR (using Wilson)

            var popCredentials = new SigningCredentials(popKey, SecurityAlgorithms.RsaSha256);
            SignedHttpRequestDescriptor signedHttpRequestDescriptor =
               new SignedHttpRequestDescriptor(
                   result.AccessToken,
                   new IdentityModel.Protocols.HttpRequestData()
                   {
                       Uri = new Uri(ProtectedUrl),
                       Method = HttpMethod.Post.ToString()
                   },
                   popCredentials);

            var signedHttpRequestHandler = new SignedHttpRequestHandler();
            string req = signedHttpRequestHandler.CreateSignedHttpRequest(signedHttpRequestDescriptor);

            // play the POP token against a webservice that accepts POP to validate the keys
            await VerifyPoPTokenAsync(
                PublicCloudConfidentialClientID,
                 ProtectedUrl,
                 HttpMethod.Post,
                 req, "pop").ConfigureAwait(false);

            // Additional check - if using the same key, the token should come from the cache
            var result2 = await confidentialApp.AcquireTokenForClient(s_keyvaultScope)
                 .WithProofOfPosessionKeyId(keyId, "pop")       // ensure tokens are bound to the key_id
                 .OnBeforeTokenRequest((data) =>
                 {
                     // add extra data to request
                     data.BodyParameters.Add("req_cnf", keyId);
                     data.BodyParameters.Add("token_type", "pop");

                     return Task.CompletedTask;
                 })
                .ExecuteAsync(CancellationToken.None)
                .ConfigureAwait(false);
            Assert.AreEqual(
                TokenSource.Cache,
                result2.AuthenticationResultMetadata.TokenSource);
        }

#if NET_CORE
        public async Task WamUsernamePasswordRequestWithPOPAsync()
        {
            var labResponse = await LabUserHelper.GetDefaultUserAsync().ConfigureAwait(false);
            string[] scopes = { "User.Read" };
            string[] expectedScopes = { "email", "offline_access", "openid", "profile", "User.Read" };

            WamLoggerValidator wastestLogger = new WamLoggerValidator();

            IPublicClientApplication pca = PublicClientApplicationBuilder
               .Create(labResponse.App.AppId)
               .WithAuthority(labResponse.Lab.Authority, "organizations")
               .WithLogging(wastestLogger)
               .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows))
               .Build();

            Assert.IsTrue(pca.IsProofOfPossessionSupportedByClient(), "Either the broker is not configured or it does not support POP.");

            var result = await pca
                .AcquireTokenByUsernamePassword(
                    scopes,
                    labResponse.User.Upn,
                    labResponse.User.GetOrFetchPassword())
                .WithProofOfPossession("nonce", HttpMethod.Get, new Uri(ProtectedUrl))
                .ExecuteAsync().ConfigureAwait(false);

            MsalAssert.AssertAuthResult(result, TokenSource.Broker, labResponse.Lab.TenantId, expectedScopes, true);

            Assert.IsTrue(wastestLogger.HasLogged);

            await VerifyPoPTokenAsync(
                labResponse.App.AppId,
                ProtectedUrl,
                HttpMethod.Get,
                result).ConfigureAwait(false);
        }
#endif

        private static X509Certificate2 GetCertificate()
        {
            X509Certificate2 cert = CertificateHelper.FindCertificateByName(TestConstants.AutomationTestCertName);
            
            if (cert == null)
            {
                throw new InvalidOperationException(
                    "Test setup error - cannot find a certificate in the My store for KeyVault. This is available for Microsoft employees only.");
            }

            return cert;
        }

        private class NoAccessHttpClientFactory : IMsalHttpClientFactory
        {
            private const string Message = "Not expecting to make HTTP requests.";

            public HttpClient GetHttpClient()
            {
                Assert.Fail(Message);
                throw new InvalidOperationException(Message);
            }
        }

        /// <summary>
        /// This calls a special endpoint that validates any POP token against a configurable HTTP request.
        /// The HTTP request is configured through headers.
        /// </summary>
        private Task VerifyPoPTokenAsync(string clientId, string requestUri, HttpMethod method, AuthenticationResult result)
        {
            return VerifyPoPTokenAsync(clientId, requestUri, method, result.AccessToken, result.TokenType);
        }

        private Task VerifyPoPTokenAsync(string clientId, string requestUri, HttpMethod method, string token, string tokenType)
        {
            Uri protectedUri = new Uri(requestUri);

            ClaimsPrincipal popClaims = IdToken.Parse(token).ClaimsPrincipal;
            string assertionWithoutShr = popClaims.FindFirst("at").Value;
            string shrM = popClaims.FindFirst("m").Value;
            Assert.AreEqual(method.ToString(), shrM, "Method mismatch");
            string shrU = popClaims.FindFirst("u").Value;
            Assert.AreEqual(protectedUri.Host, shrU, "Host mismatch");
            string shrP = popClaims.FindFirst("p").Value;
            Assert.AreEqual(protectedUri.LocalPath, shrP, "Path mismatch");
            string ts   = popClaims.FindFirst("ts").Value;
            Assert.IsTrue(int.TryParse(ts, out int _), "timestamp");
            string cnf  = popClaims.FindFirst("cnf").Value;
            Assert.IsNotNull(cnf);
            ClaimsPrincipal innerTokenClaims = IdToken.Parse(assertionWithoutShr).ClaimsPrincipal;
            string reqCnf = innerTokenClaims.FindFirst("cnf").Value;
            Assert.IsNotNull(reqCnf);

            return Task.Delay(0);
            // POP validation endpoint is down
            // uncomment code below https://github.com/AzureAD/microsoft-authentication-library-for-dotnet/issues/4264

            //var httpClient = new HttpClient();
            //HttpResponseMessage response;
            //var request = new HttpRequestMessage(HttpMethod.Post, PoPValidatorEndpoint);

            //var authHeader = new AuthenticationHeaderValue(tokenType, token);

            //request.Headers.Add("Secret", _popValidationEndpointSecret);
            //request.Headers.Add("Authority", "https://sts.windows.net/72f988bf-86f1-41af-91ab-2d7cd011db47/");
            //request.Headers.Add("ClientId", clientId);
            //request.Headers.Authorization = authHeader;

            //// the URI the POP token is bound to
            //request.Headers.Add("ShrUri", requestUri);

            //// the method the POP token in bound to
            //request.Headers.Add("ShrMethod", method.ToString());

            //response = await httpClient.SendAsync(request).ConfigureAwait(false);

            //Assert.IsTrue(response.IsSuccessStatusCode);
        }

        private string _inMemoryCache = "{}";
        private void ConfigureInMemoryCache(IConfidentialClientApplication pca)
        {
            pca.AppTokenCache.SetBeforeAccess(notificationArgs =>
            {
                byte[] bytes = Encoding.UTF8.GetBytes(_inMemoryCache);
                notificationArgs.TokenCache.DeserializeMsalV3(bytes);
            });

            pca.AppTokenCache.SetAfterAccess(notificationArgs =>
            {
                if (notificationArgs.HasStateChanged)
                {
                    byte[] bytes = notificationArgs.TokenCache.SerializeMsalV3();
                    _inMemoryCache = Encoding.UTF8.GetString(bytes);
                }
            });
        }

        private static RsaSecurityKey CreateRsaSecurityKey()
        {
#if NET_FX
            RSA rsa = RSA.Create(2048);
#else
            RSA rsa = new RSACryptoServiceProvider(2048);
#endif
            // the reason for creating the RsaSecurityKey from RSAParameters is so that a SignatureProvider created with this key
            // will own the RSA object and dispose it. If we pass a RSA object, the SignatureProvider does not own the object, the RSA object will not be disposed.
            RSAParameters rsaParameters = rsa.ExportParameters(true);
            RsaSecurityKey rsaSecuirtyKey = new RsaSecurityKey(rsaParameters) { KeyId = CreateRsaKeyId(rsaParameters) };
            rsa.Dispose();
            return rsaSecuirtyKey;
        }

        private static string CreateRsaKeyId(RSAParameters rsaParameters)
        {
            byte[] kidBytes = new byte[rsaParameters.Exponent.Length + rsaParameters.Modulus.Length];
            Array.Copy(rsaParameters.Exponent, 0, kidBytes, 0, rsaParameters.Exponent.Length);
            Array.Copy(rsaParameters.Modulus, 0, kidBytes, rsaParameters.Exponent.Length, rsaParameters.Modulus.Length);
            using (var sha2 = SHA256.Create())
                return Base64UrlEncoder.Encode(sha2.ComputeHash(kidBytes));
        }
    }
}
