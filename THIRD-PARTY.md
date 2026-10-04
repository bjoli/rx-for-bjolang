# Third-party code

Much of this library is a translation into C# of code from the Rust crates
`regex-syntax` and `regex-automata`:

- Source: https://github.com/rust-lang/regex
- Ported from commit `72d650cb0a880a01ab6dc2137c0888e8f89740f7`
  (`regex-syntax` 0.8.11, `regex-automata` 0.4.18)
- Copyright (c) 2014 The Rust Project Developers
- License: MIT OR Apache-2.0. The texts are in `LICENSE-MIT` and
  `LICENSE-APACHE` in this directory.

Each ported file says which upstream file it comes from.

The test data in `tests/BjoRx.Tests/testdata/` is copied from the same
repository's `testdata/` directory, under the same license.
