# Security model and deployment cautions

This repository contains remote-support software. Treat code signing, release integrity, technician authentication, server hardening, and customer consent as security-critical parts of the product.

## MVP safeguards

- Customer initiates the connection outbound.
- Session codes expire and can be redeemed only once.
- Short codes are HMACed before database storage.
- Live agent credentials use 256 bits of randomness; only their SHA-256 digests are stored.
- Explicit terms/consent is required before code redemption.
- Technician web authentication uses a Secure/HttpOnly/SameSite=Strict signed cookie.
- Login and code enrollment are rate-limited in process.
- The Go service listens on loopback by default and is exposed through Apache HTTPS/WSS.
- The broker does not intentionally persist remote screen frames.
- The customer executable does not install a permanent remote-access password.
- The customer can terminate access by closing the app.
- Administrator elevation invokes normal Windows UAC; it is not bypassed.

## Before production use

1. Digitally sign the Windows executable with a TechFinity code-signing identity.
2. Add real multi-user technician accounts, MFA, RBAC, revocation, and password hashing/SSO. The environment-backed single admin account is an MVP bootstrap mechanism.
3. Add durable distributed rate limiting if more than one application server is introduced.
4. Perform an independent security review of the WebSocket broker, input handling, deployment script, and Windows process/elevation boundary.
5. Add structured audit metadata and retention controls appropriate for customer agreements.
6. Add release signing/checksums and a controlled update channel.
7. Add automated dependency scanning and operating-system patching.
8. Keep the service behind HTTPS. Do not expose port 8787 publicly.

## Out of scope by design

The current agent cannot control the Windows secure desktop, does not inject the secure attention sequence, and does not install a SYSTEM service. Do not weaken UAC or disable secure-desktop policy as a workaround.
