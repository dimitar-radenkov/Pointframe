# Security Policy

## Supported Versions

Only the latest release receives security fixes.

## Reporting a Vulnerability

Please **do not** open a public GitHub issue for security vulnerabilities.

Use GitHub's [private vulnerability reporting](https://github.com/dimitar-radenkov/SnippingTool/security/advisories/new) to submit a report confidentially. You will receive a response within 7 days.

Please include:

- A description of the vulnerability
- Steps to reproduce it
- Potential impact

## Scope

Pointframe is a local, offline Windows desktop application. It does not transmit screenshots, recordings, or any user data to external servers. The only outbound network request is the optional **Check for Updates** feature, which queries the GitHub Releases API (`api.github.com`) — no screenshot or personal data is sent.
