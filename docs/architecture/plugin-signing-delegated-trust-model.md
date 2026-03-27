# Plugin Signing Delegated Trust Model

## Purpose

Define a signing and trust architecture that avoids a single global private key while keeping large multi-team repositories operationally manageable.

This design addresses two competing goals:
- Minimize blast radius for key compromise.
- Avoid full-repo re-signing and broad outage when one signer is compromised.

## Problem statement

A single global signing key is simple but creates a catastrophic single point of failure.

A single key per repository reduces global blast radius, but for large repositories it is still too coarse:
- A compromised signer can affect all plugins in that repository.
- Rotation and incident response can force broad re-signing and deployment churn.

## Threat model

Primary threats:
- Private key exfiltration from CI or developer workstation.
- Insider misuse by a rogue contributor.
- Unauthorized signing due to compromised automation credentials.
- Replay of old signed artifacts after policy changes.
- Mismatch between signed manifest and shipped artifact.

Security goals:
- Scope compromise to the smallest practical set of plugins.
- Allow rapid targeted revocation and rotation.
- Preserve verification in offline and degraded network modes.
- Keep policy enforceable at runtime with fail-closed behavior in production.

## Design overview

Use a delegated trust model per repository:

1. Repository root trust anchor (offline)
- Root key is not used for routine release signing.
- Root key signs delegation metadata that authorizes scoped online signers.

2. Delegated signer keys (online, scoped)
- One delegated key per team, plugin family, or plugin id pattern.
- CI release jobs sign plugin manifests with delegated keys only.
- Delegated keys are short-lived and regularly rotated.

3. Runtime trust policy
- Runtime trusts repository root public keys (or root-signed policy bundle).
- Runtime validates delegation chain and signer scope before accepting plugin signatures.

Result:
- A compromised delegated signer impacts only its authorized scope.
- No full-repository re-signing is required unless repository root trust is compromised.

## Signing and metadata model

### Manifest signature metadata

Each plugin manifest signature should include:
- algorithm: example `rsa-sha256`.
- value: base64 signature over canonical payload.
- keyId: signer key identifier.
- issuedAtUtc: signing timestamp.
- optional expiresAtUtc: signature validity bound.

### Delegation metadata (root-signed)

Repository publishes a delegation file signed by root that contains:
- repositoryId.
- delegations:
  - keyId.
  - publicKeyPem.
  - scopes (exact plugin ids or prefix patterns).
  - validFromUtc.
  - validUntilUtc.
  - status (active, retired, revoked).
- version and issuedAtUtc.

### Canonical signed payload

The signed payload should include at minimum:
- plugin id.
- plugin version.
- protocol.
- repository id.
- artifact digest (zip/package hash).
- manifest digest.

Including digests prevents manifest/artifact substitution attacks.

## Verification pipeline

For install and load:

1. Parse manifest and signature block.
2. Resolve repositoryId from source context (catalog/repository metadata), not only manifest self-claim.
3. Resolve signer by keyId in repository delegation set.
4. Verify repository root signature over delegation metadata.
5. Verify signer is active, time-valid, and not revoked.
6. Verify signer scope authorizes the target plugin id.
7. Verify plugin signature over canonical payload.
8. Verify artifact/manifest digests match downloaded content.
9. Enforce policy:
- Production: fail closed on unknown key, invalid chain, revoked signer, scope violation, signature failure.
- Development: optional warn-only mode for local iteration.

## Key lifecycle and operations

### Root key handling

- Stored offline or HSM-backed with strict access control.
- Used only to sign delegation updates and emergency trust operations.
- Rotation is rare and planned with broad compatibility windows.

### Delegated key handling

- Stored in CI secret manager or HSM/KMS-backed signer.
- Never committed to repository.
- Short lifetime preferred (for example 30-90 days).
- Rotation is routine and automated where possible.

### Revocation workflow

When delegated key compromise is suspected:
1. Mark signer as revoked in delegation metadata.
2. Publish updated root-signed delegation file.
3. Trigger trust policy refresh in clients.
4. Re-sign only affected plugin scope with replacement key.
5. Quarantine or block compromised versions as needed.

No repository-wide re-sign is required.

## Policy and governance model

Recommended ownership split:
- Central security/platform team owns runtime verification policy and root trust governance.
- Repository maintainers own delegated signer issuance and scoped release pipelines.
- Teams own scoped signers aligned to plugin ownership boundaries.

This avoids central private-key bottlenecks while preserving strong policy control.

## Tradeoffs

Benefits:
- Strong blast-radius reduction.
- Fast targeted incident response.
- Better support for large multi-team repositories.
- Independent signer rotation without ecosystem-wide disruption.

Costs:
- Additional metadata and policy distribution complexity.
- Need for robust delegation and revocation tooling.
- More explicit operational runbooks.

The security and operational resilience gains justify this complexity for plugin ecosystems.

## Migration plan (incremental)

1. Asymmetric baseline
- Complete migration from symmetric signing to asymmetric verification.
- Ensure only public key material exists on client/runtime.

2. Key identity support
- Add `keyId` and signer metadata to manifest signatures.
- Add runtime lookup by repositoryId + keyId.

3. Delegation file
- Introduce root-signed delegation metadata per repository.
- Add runtime chain validation and scope checks.

4. Digest binding
- Bind signature payload to manifest and artifact digests.
- Enforce digest verification in install pipeline.

5. Policy hardening
- Staged rollout from monitor-only to fail-closed in production.

6. Incident readiness
- Add documented revocation and rotation drills.
- Add audit logging for signer decisions and failures.

## Open implementation decisions

- Algorithm standardization: RSA now vs Ed25519 target.
- Delegation file schema format and canonicalization rules.
- Trust policy delivery: bundled, remote signed updates, or hybrid.
- Refresh cadence and offline cache behavior.
- Backward compatibility window for legacy signatures.

## Success criteria

- Compromise of one delegated signer cannot sign out-of-scope plugins.
- Revocation of one delegated signer requires no repository-wide re-sign.
- Runtime rejects unknown, revoked, or out-of-scope signer keys in production.
- Audit logs clearly identify repositoryId, keyId, decision outcome, and reason.
