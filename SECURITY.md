<!-- SPDX-License-Identifier: GPL-3.0-only -->

# Security policy

## Supported versions

Cadenza is pre-release. Security fixes are made only on the current default
branch and, when explicitly identified, the active release-candidate branch.
Old commits, forks, and unpublished binaries are not supported release lines.

## Report privately

Use the repository's **Security** tab to open a private vulnerability report:

<https://github.com/RJCHAN21/Cadenza-development/security/advisories/new>

Do not open a public issue containing an active vulnerability, malicious test
file, exploit steps, private score, local profile, or personal data. If GitHub
private reporting is unavailable, contact the repository owner through their
GitHub profile and ask for a private channel without including exploit details
in the first message.

Include:

- affected commit and Windows/.NET/WebView2 versions;
- the input boundary involved (MusicXML, MXL, MIDI, WebView, profile, or file
  path);
- minimal reproduction steps and observed impact;
- a small safe proof of concept or original fixture, if appropriate;
- whether the issue is already public or actively exploited;
- any suggested mitigation.

The maintainer will acknowledge the report when available, reproduce and
triage it, coordinate a fix and disclosure when warranted, and credit the
reporter if requested. No fixed response or release deadline is promised for
this volunteer, pre-release project.

## In scope

- unsafe archive extraction or decompression/resource exhaustion;
- XML entity, parser, or path traversal behavior;
- malformed MIDI leading to unbounded work, allocation, or unsafe device use;
- trust-boundary bypass in the local WebView2 renderer or message bridge;
- arbitrary file access through library, reference, or persistence paths;
- exposure or corruption of user-local profiles, history, or imported scores;
- dependency or packaging issues that materially affect Cadenza users.

General bugs, unsupported notation, grading accuracy, and feature requests are
not security reports unless they cross a security boundary.
