---
title: "AL Runner"
weight: 1
---

Run Business Central AL unit tests in milliseconds. No service tier, no Docker,
no SQL Server, no license.

AL Runner loads Business Central's own compiler and runtime assemblies in
process and executes your AL test codeunits directly against them. Your tests
run against real Base Application and System Application code, not against a
mock — the runner replaces the host Business Central usually needs, not the
business logic you are testing.

## Where to start

- **[Getting started](getting-started.md)** — install it and run a test.
- **[Writing tests](writing-tests.md)** — what a bundle is and how tests are structured.
- **[What works](what-works.md)** — the supported surface, and what is deliberately out of scope.
- **[Command line reference](cli-reference.md)** — the flags you will actually use.
- **[Troubleshooting](troubleshooting.md)** — what the common failures mean.

## What it is for

Fast feedback. A Business Central test suite that normally needs a container and
several minutes runs here in seconds, on Windows, Linux or macOS, with nothing
installed but the .NET SDK. That makes AL tests something you can run on every
save and in ordinary CI, rather than something you run occasionally because it
is expensive.

It is not a replacement for testing against a real environment. Anything that
needs the service tier itself — sending mail, calling a web service, rendering a
report, publishing an endpoint — is out of scope by design and refuses loudly
rather than quietly returning a default. [What works](what-works.md) draws the
line.
