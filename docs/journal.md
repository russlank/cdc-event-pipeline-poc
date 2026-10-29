# Journal: What Broke and How It Was Fixed

Document id: `journal`
Status: active
Last updated: 2026-10-29

## Purpose

An honest log of every failure met while building this PoC: symptom, cause,
and fix, newest first. Failure drills add their results here as well.

## Entries

### 2026-10-29: Empty test project fails the test run

- **Symptom:** `dotnet test` exited with code 8 although every test passed.
- **Cause:** Microsoft.Testing.Platform treats "zero tests ran" as a failure
  (exit code 8). The integration-test project existed but had no tests yet.
- **Fix:** removed the empty project; it is added together with its first
  test instead of as a placeholder.

### 2026-10-29: Strict analyzers versus project conventions

- **Symptom:** the first Release build failed with CA1873, ASP0027, CA1707,
  and, once code style was enforced, IDE0160, IDE0022, IDE0008, IDE0058,
  IDE0290, and IDE0005.
- **Cause:** `.editorconfig` raises every analyzer diagnostic to a warning and
  warnings are errors. Without explicit style preferences the defaults
  disagree with modern C# (block-scoped namespaces, explicit types
  everywhere). CA1707 forbids underscores, but test names follow
  `Method_State_ExpectedBehavior`. ASP0027: .NET 10 already makes `Program`
  public. CA1873: an argument was formatted before the logger knew whether
  the level was enabled. IDE0005 at build time needs `GenerateDocumentationFile`.
- **Fix:** explicit style preferences in `.editorconfig`; IDE0022 and IDE0058
  off; CA1707 off for test projects only; `public partial class Program`
  removed; the identity object passed to the log method instead of its
  string; `GenerateDocumentationFile` on with CS1591 off (no public packages).
  Code style is now enforced by the build, so build and `dotnet format`
  agree.

### 2026-10-29: `dotnet test` refuses the VSTest runner

- **Symptom:** `Testing with VSTest target is no longer supported by
  Microsoft.Testing.Platform on .NET 10 SDK and later.`
- **Cause:** xunit.v3 4.x runs on Microsoft.Testing.Platform; the .NET 10 SDK
  needs an explicit opt-in to run it through `dotnet test`.
- **Fix:** `"test": { "runner": "Microsoft.Testing.Platform" }` in
  `src/global.json`; removed the VSTest packages (`Microsoft.NET.Test.Sdk`,
  `xunit.runner.visualstudio`). Related: xunit.v3 4.x does not add
  `using Xunit;` implicitly, so the test projects declare it.

### 2026-10-29: Topic init job exits with code 1

- **Symptom:** `kafka-init` created both topics, then failed.
- **Cause:** `kafka-topics.sh --describe` accepts one `--topic`; the job
  passed two.
- **Fix:** describe with the pattern `'hr\..*'`. The job is idempotent
  (`--if-not-exists`), so rerunning it is safe.

### 2026-10-29: Schema Registry never becomes healthy

- **Symptom:** `docker compose up --wait` failed with "container
  schema-registry is unhealthy" although its log said "Server started,
  listening for requests".
- **Cause:** the health check used `curl`; the `cp-schema-registry` 8.x image
  contains neither `curl` nor `wget` (`curl: command not found`, exit 127).
- **Fix:** an HTTP request through bash `/dev/tcp`, checking for status 200 on
  `/subjects`.

### 2026-10-29: KRaft cluster id

- **Symptom:** caught in review before the first start: the compose file had
  a readable `CLUSTER_ID`.
- **Cause:** KRaft requires a base64-encoded 16-byte UUID (22 characters).
- **Fix:** a fixed id derived from a UUIDv5, so the stack starts the same way
  every time.
