# Contributing

Requires the .NET 10 SDK. Azure Functions Core Tools v4 is needed only for
running the HTTP sample.

```bash
dotnet build FunctionsWebAppHost.slnx --configuration Release
dotnet test tests/FunctionsWebAppHost.Tests/FunctionsWebAppHost.Tests.csproj --configuration Release --no-build
dotnet pack src/FunctionsWebAppHost/FunctionsWebAppHost.csproj --configuration Release --no-build --output artifacts
```

Keep changes focused and add tests for changed behavior. Do not commit build
outputs, credentials or local settings. See the [sample](samples/FunctionsWebAppHostTest/README.md)
for real Functions ingress checks.

Contributions are licensed under [Apache 2.0](LICENSE).

## Publishing

1. Confirm the `FunctionsWebAppHost` package ID is available on NuGet.org or
   owned by your account. GitHub ownership does not grant NuGet ownership.
2. Create a GitHub environment named `nuget`, restrict it to release tags and
   configure required reviewers where available.
3. On NuGet.org, configure a
   [Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
   policy with repository owner `brendan-nobadthing`, repository
   `FunctionsWebAppHost`, workflow file `ci.yml` and environment `nuget`.
   Use package glob `FunctionsWebAppHost` and allow publishing new packages
   and package versions.
4. In the GitHub `nuget` environment, add an environment **variable** named
   `NUGET_USER` containing your NuGet.org profile username, not your email.
   No long-lived `NUGET_API_KEY` secret is required.
5. Set the next development version in the library project, then commit and
   push the release changes.
6. Push a release tag, for example:

   ```bash
   git tag v0.1.0-alpha.1
   git push origin v0.1.0-alpha.1
   ```

The workflow builds the solution, runs tests, packs with the tag's version and
publishes only after those checks pass. A normal branch push or pull request
uploads package artifacts but never publishes. NuGet versions are immutable;
use a new tag/version for each release. The push also uploads the matching
symbol package. The publish job uses GitHub OIDC and `NuGet/login` to obtain a
short-lived API key immediately before pushing.
