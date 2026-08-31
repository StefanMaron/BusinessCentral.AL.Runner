# `--test-data` hydration fixture

End-to-end proof for issue #2258: rows decoded out of a BC `.bak` land in the in-memory
store and read back through ordinary AL `Record` calls with the right values.

**CI does not run this bundle, and that is deliberate.** It only passes with `--test-data`
and a BC sandbox backup on the machine (~1 GB, shipped inside the sandbox artifact). CI runs
`tests/runner-extras/` wholesale, without the flag — a bundle asserting hydrated rows would
fail there by construction rather than prove anything. Hence its own directory outside that
tree.

Run it locally:

```bash
export AL_RUNNER_BCBAK=~/.cache/al-runner/bcbak/bcbak       # or put `bcbak` on PATH
dotnet run --project AlRunner -c Release -- \
    tests/test-data-fixture \
    --package-cache "$HOME/.al-runner/platform-apps" \
    --test-data="$HOME/.bcartifacts.cache/sandbox/<version>/w1/BusinessCentral-W1.bak" \
    --test-data-company "CRONUS International Ltd_"
```

Without `--test-data` the tests fail, loudly and on purpose: the whole claim is that the
flag is what puts the rows there.

The runner-side mechanism (flag parsing, backup resolution, the install-baseline cache key,
the exclusion rules, value conversion) is pinned by `AlRunner.Tests/TestDataProvisioningTests.cs`,
which runs on every CI leg and needs neither the backup nor the reader.
