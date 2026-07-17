# Code signing the Windows executables

> **Status: recommended next step.** winget-tui-sharp is now used as a daily winget TUI, so
> the SmartScreen friction below hits real users on every fresh download — this is worth
> acting on, not just researching. This doc captures the options investigated, what they
> cost, what they buy you, and the recommended path: apply for **SignPath.io**'s OSS
> sponsorship first (free, fits a small-maintenance project); fall back to **Azure Trusted
> Signing** (~$10/mo) if sponsorship is declined or too slow. If you're a contributor looking
> to drive this forward, this is the briefing.

## What the problem actually is

When a Windows user downloads `winget-tui-sharp.exe`:

1. **Mark-of-the-Web (MOTW)** — the browser tags the file as "from the internet." On first run, Windows blocks it with a SmartScreen prompt: *"Windows protected your PC."* The user has to click *More info → Run anyway*. This is the prompt we want to eliminate.

2. **Microsoft Defender SmartScreen reputation** — separate from MOTW. SmartScreen builds a reputation score per signing identity. An unsigned binary has no identity, so it has no reputation. A signed binary builds reputation as more users run it without issue.

3. **Antivirus false positives** — unsigned native AOT binaries occasionally trip heuristic AV scanners. Signing meaningfully reduces (doesn't eliminate) this.

Code signing addresses (1) immediately for **EV** certs, eventually for **OV** certs, and (2) over time for both. It can't perfectly solve (3) but helps.

## The five options

### 1. Azure Trusted Signing — *what upstream `shanselman/winget-tui` uses*

Microsoft's managed cloud signing service. No hardware tokens, no certificate management — you authenticate via Entra ID, the cert lives in Azure, the build agent calls a signing endpoint.

| | |
|---|---|
| **Cost** | $9.99 USD per month (Basic), $99.99 per month (Premium) |
| **Cert type** | Microsoft-issued OV (Organization Validation), backed by Microsoft's CA |
| **SmartScreen** | Builds reputation over time. Not instant. |
| **Setup** | Subscription identity validation (~2–4 weeks initial), then turnaround is fast |
| **CI integration** | First-class [`azure/trusted-signing-action`](https://github.com/Azure/trusted-signing-action) GitHub Action |
| **Constraint** | Requires an Azure tenant and an organization-or-individual identity that passes Microsoft's verification |
| **What upstream does** | The Rust `shanselman/winget-tui` uses this; see `.github/workflows/build.yml` in upstream — they sign all release exes |

**Recommendation: best fit if you want to match upstream's signing story exactly and don't mind a small recurring cost.**

### 2. SignPath.io — *free for verified OSS projects*

Third-party signing service that brokers code-signing certs and runs the actual signing in their cloud. They offer a sponsored "Foundation" tier for open-source projects.

| | |
|---|---|
| **Cost** | Free for OSS that meets their criteria; commercial tiers from ~$10/mo |
| **Cert type** | OV via Certum / GlobalSign (publicly trusted CA) |
| **SmartScreen** | Builds reputation over time. Same warm-up as #1. |
| **Setup** | Apply, get approved (couple of weeks for OSS sponsorship), then GitHub App + project policy setup |
| **CI integration** | Their [`signpath-github-action`](https://github.com/SignPath/github-action-submit-signing-request) action |
| **Constraint** | OSS sponsorship requires project to be public, OSI-approved license, "responsible maintainership" (vague). POC projects sometimes get declined. |
| **Reputation note** | They publish a guide to building SmartScreen reputation faster — basically, ship and let users run it |

**Recommendation: apply now.** This is the first move — free, and fits the project's actual maintenance capacity. While the application is pending, keep shipping unsigned with a clear `Unblock-File` doc (see below).

### 3. EV (Extended Validation) code-signing cert + Azure Key Vault

The traditional path that **gets you instant SmartScreen reputation** — no warm-up period.

| | |
|---|---|
| **Cost** | $300–700/year (cert) + ~$10/month Azure Key Vault HSM |
| **Cert type** | EV (Extended Validation), the gold standard |
| **SmartScreen** | **Instant trust.** No warm-up. This is the *only* option that does this. |
| **Setup** | EV cert: identity verification + hardware token shipped to your physical address. Hardware token is awkward for CI. Workaround: Azure Key Vault HSM (`KEYFACTOR` signs, etc.) — pricier but cloud-signable. |
| **CI integration** | [`azuresigntool`](https://github.com/vcsjones/AzureSignTool) + `azure/login` works, OR commercial wrappers like CodeSignTool |
| **Vendors** | DigiCert, Sectigo, SSL.com, Certum |
| **Constraint** | EV cert holder must be a legal entity (individual EV is rare and pricier). Hardware token + cloud HSM combo is brittle the first time you set it up. |

**Recommendation: best fit if SmartScreen friction *now* outweighs $400–800/year.** Only choice if you absolutely cannot ship a "Unknown publisher" warning on first run.

### 4. Self-signed certificate

Don't. SmartScreen treats self-signed as unsigned (you're not in any CA's trust chain). The only thing self-signing buys you is integrity verification *if the user pre-installs your cert as a trusted root* — which no real user will do.

### 5. GitHub Attestations / Sigstore

Free, GitHub-native cryptographic provenance — proves a binary was built from a specific commit of a specific repo by a specific workflow.

| | |
|---|---|
| **Cost** | Free |
| **What it does** | Provenance, supply-chain trust |
| **What it doesn't do** | **It does not unblock SmartScreen.** It's not a code-signing cert. Windows doesn't check Sigstore. |
| **Worth doing?** | Yes, *in addition to* a real signing strategy. Adds verifiable build-source attestation for security-minded users. |

Documented for completeness; doesn't solve the prompt-on-download problem on its own.

## Workaround for users on the unsigned binary (Phase 1)

PowerShell:

```powershell
Unblock-File -Path .\winget-tui-sharp.exe
```

Or right-click the exe → *Properties* → *Unblock* checkbox → *OK*. After unblocking, SmartScreen will still warn on the very first run; click *More info → Run anyway* once and it remembers.

For users who download via `gh release download`, this is a one-liner per machine.

## Implementation sketch (Azure Trusted Signing)

If you adopt Azure Trusted Signing, the release workflow change is small. Add to `.github/workflows/release.yml` after the AOT publish step, before the package step:

```yaml
- uses: azure/login@v2
  with:
    creds: ${{ secrets.AZURE_CREDENTIALS }}

- uses: azure/trusted-signing-action@v0.5.0
  with:
    azure-tenant-id: ${{ secrets.AZURE_TENANT_ID }}
    azure-client-id: ${{ secrets.AZURE_CLIENT_ID }}
    azure-client-secret: ${{ secrets.AZURE_CLIENT_SECRET }}
    endpoint: https://wus2.codesigning.azure.net/   # adjust to your region
    signing-account-name: <your-signing-account>
    certificate-profile-name: <your-profile>
    files-folder: ${{ github.workspace }}/publish/${{ matrix.rid }}
    files-folder-filter: exe
    file-digest: SHA256
    timestamp-rfc3161: http://timestamp.acs.microsoft.com
    timestamp-digest: SHA256
```

The four `AZURE_*` secrets come from the Entra ID app registration you create during Azure Trusted Signing setup. Once added, every release-built `.exe` gets signed before packaging.

## References

- Microsoft, [Azure Trusted Signing overview](https://learn.microsoft.com/azure/trusted-signing/overview)
- [SignPath.io for Open Source](https://about.signpath.io/products/open-source)
- Vincent Cui, [AzureSignTool](https://github.com/vcsjones/AzureSignTool) — EV signing via Azure Key Vault
- [GitHub Attestations](https://docs.github.com/en/actions/security-for-github-actions/using-artifact-attestations) — build provenance, complementary to signing
- Upstream reference: [`shanselman/winget-tui/.github/workflows/build.yml`](https://github.com/shanselman/winget-tui/blob/main/.github/workflows/build.yml)
