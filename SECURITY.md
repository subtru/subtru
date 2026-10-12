# Security Policy

## Supported versions

Security fixes are released only in the newest version. Older versions do not
receive backported security updates, so self-hosters should upgrade to the
latest release.

| Version | Supported |
| --- | --- |
| Latest release | Yes |
| Older releases | No |

## Reporting a vulnerability

Do not report suspected vulnerabilities in a public GitHub issue, discussion,
or pull request.

Use GitHub's private vulnerability reporting from this repository's
**Security** tab. If that option is unavailable, email `support@subtru.com`
with `[SECURITY]` in the subject line. Include the affected version, impact,
reproduction steps, and any suggested mitigation. Do not include live customer
credentials or private customer data; use minimal test data where possible.

We aim to acknowledge reports within five business days. After confirming the
issue, we will coordinate remediation and disclosure with the reporter. Please
allow a reasonable amount of time for a fix before publishing details.

Relevant reports include authentication or authorization bypasses, webhook
verification failures, exposure of secrets or subscription data, cryptographic
key-handling problems, injection vulnerabilities, and container or deployment
issues caused by SubTru defaults.

Reports about vulnerabilities in Stripe, Apple, Google, PostgreSQL, or a
hosting provider should be sent to the affected provider unless SubTru's use of
that service creates the vulnerability.
