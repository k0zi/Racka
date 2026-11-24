# Third-party notices

Racka's own source code is licensed under the MIT license in LICENSE.
The `Racka` NuGet package (the `racka` dotnet tool) redistributes the third-party components listed below, and
the source tree contains code adapted from another project. These components keep their own licenses, and the
full license texts are in `licenses/`. Listing them here does not relicense them or imply endorsement.

## Copied into the source tree

| Component | Source | License / local copy |
| --- | --- | --- |
| Flow engine in `src/HerdFlow` (a C# port of PocketFlow's `Node`, `Flow`, batch and async classes) | PocketFlow, Copyright (c) 2024 Zachary Huang, https://github.com/The-Pocket/PocketFlow | MIT: licenses/PocketFlow-MIT.txt |

## Redistributed libraries (framework-dependent dotnet tool)

The package runs on the user's installed .NET and ASP.NET Core runtimes; those runtimes are not redistributed.

| Component | Source | License / local copy |
| --- | --- | --- |
| Anthropic C# SDK (Anthropic) | https://github.com/anthropics/anthropic-sdk-csharp | Apache-2.0: licenses/Anthropic-Apache-2.0.txt; contributions from before the change to Apache-2.0 remain MIT: licenses/Anthropic-MIT.txt |
| OpenAI .NET library (OpenAI) | https://github.com/openai/openai-dotnet | MIT: licenses/OpenAI-MIT.txt |
| MCP C# SDK (ModelContextProtocol, ModelContextProtocol.Core) | https://github.com/modelcontextprotocol/csharp-sdk | Apache-2.0 AND MIT (Apache-2.0, with contributions not yet relicensed under MIT): licenses/ModelContextProtocol-Apache-2.0-AND-MIT.txt; upstream component notices: licenses/ModelContextProtocol-THIRD-PARTY-NOTICES.txt |
| YamlDotNet | https://github.com/aaubry/YamlDotNet | MIT: licenses/YamlDotNet-MIT.txt |
| Discord.Net (Discord.Net.Core, Discord.Net.Rest, Discord.Net.WebSocket, Discord.Net.Dave) | https://github.com/discord-net/Discord.Net | MIT: licenses/Discord.Net-MIT.txt |
| Telegram.Bot | https://github.com/TelegramBots/Telegram.Bot | MIT: licenses/Telegram.Bot-MIT.txt; upstream component notices: licenses/Telegram.Bot-THIRD-PARTY-NOTICES.txt |
| Microsoft 365 Agents SDK (Microsoft.Agents.Authentication, Microsoft.Agents.Authentication.Msal, Microsoft.Agents.Builder, Microsoft.Agents.Connector, Microsoft.Agents.Core, Microsoft.Agents.Hosting.AspNetCore, Microsoft.Agents.Storage, Microsoft.Agents.Storage.Transcript) | https://github.com/microsoft/Agents-for-net | MIT: licenses/Microsoft-Agents-SDK-MIT.txt |
| Azure SDK for .NET (Azure.Core, Azure.Identity, Azure.Storage.Blobs, Azure.Storage.Common, System.ClientModel) | https://github.com/Azure/azure-sdk-for-net | MIT: licenses/Azure-SDK-MIT.txt; upstream notices: licenses/Azure-SDK-NOTICE.txt |
| MSAL.NET (Microsoft.Identity.Client, Microsoft.Identity.Client.Extensions.Msal) | https://github.com/AzureAD/microsoft-authentication-library-for-dotnet | MIT: licenses/MSAL.NET-MIT.txt; upstream component notices: licenses/MSAL.NET-THIRD-PARTY-NOTICES.txt |
| Microsoft Identity Web (Microsoft.Identity.Web.Certificateless) | https://github.com/AzureAD/microsoft-identity-web | MIT: licenses/Microsoft-Identity-Web-MIT.txt |
| IdentityModel (Microsoft.IdentityModel.Abstractions, .JsonWebTokens, .Logging, .LoggingExtensions, .Protocols, .Protocols.OpenIdConnect, .Tokens, .Validators, System.IdentityModel.Tokens.Jwt) | https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet | MIT: licenses/IdentityModel-MIT.txt; upstream notices: licenses/IdentityModel-NOTICE.txt, licenses/IdentityModel-THIRD-PARTY-NOTICES.txt |
| ASP.NET Core (Microsoft.AspNetCore.Authentication.JwtBearer) | https://github.com/dotnet/aspnetcore | MIT: licenses/ASP.NET-Core-MIT.txt; upstream component notices: licenses/ASP.NET-Core-THIRD-PARTY-NOTICES.txt |
| .NET libraries (System.CommandLine, Microsoft.Bcl.AsyncInterfaces, Microsoft.Bcl.Cryptography, Microsoft.Bcl.Memory, System.Configuration.ConfigurationManager, System.IO.Hashing, System.Memory.Data, System.Runtime.Caching, System.Security.Cryptography.ProtectedData) | https://github.com/dotnet/runtime (built from https://github.com/dotnet/dotnet) | MIT: licenses/dotnet-runtime-MIT.txt; upstream component notices: licenses/dotnet-runtime-THIRD-PARTY-NOTICES.txt |
| Microsoft.Extensions.AI.Abstractions | https://github.com/dotnet/extensions | MIT: licenses/dotnet-extensions-MIT.txt; upstream component notices: licenses/dotnet-extensions-THIRD-PARTY-NOTICES.txt |
| Newtonsoft.Json | https://github.com/JamesNK/Newtonsoft.Json | MIT: licenses/Newtonsoft.Json-MIT.txt |

## Not redistributed

The following are used only to build or test Racka, or come with the user's .NET installation, and are not
included in the NuGet package:
Nerdbank.GitVersioning, xunit (xunit, xunit.abstractions, xunit.analyzers, xunit.assert, xunit.core,
xunit.extensibility.core, xunit.extensibility.execution, xunit.runner.visualstudio), coverlet.collector,
Microsoft.NET.Test.Sdk, Microsoft.CodeCoverage, Microsoft.TestPlatform.ObjectModel, Microsoft.TestPlatform.TestHost,
the .NET runtime and the ASP.NET Core shared framework (including Microsoft.Extensions.Caching.Abstractions,
Microsoft.Extensions.Configuration*, Microsoft.Extensions.DependencyInjection*, Microsoft.Extensions.Diagnostics*,
Microsoft.Extensions.FileProviders.Abstractions, Microsoft.Extensions.Hosting.Abstractions, Microsoft.Extensions.Http,
Microsoft.Extensions.Logging*, Microsoft.Extensions.Options*, Microsoft.Extensions.Primitives and
System.Diagnostics.EventLog), and the native libdave library that Discord.Net.Dave binds to (only loaded for voice,
which Racka does not use).

## Maintenance

When you add copied code or assets, or change the dependencies, record the source, version,
author and license here, and update the license copies in `licenses/`. Before a release, check
the publish output (`dotnet publish src/Racka.Host -c Release`) for new packages and native libraries.
