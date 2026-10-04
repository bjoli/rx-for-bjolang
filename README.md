# BjoRx

A linear-time regular expression engine for C#, matching UTF-8 bytes. It
is a translation of parts of Rust's
[`regex-syntax` and `regex-automata`](https://github.com/rust-lang/regex)
crates, reduced to what Bjolang's `(std rx)` library needs: literals,
classes of scalars, concatenation, alternation, greedy and lazy
repetition, capture groups and the `\A`/`\z` anchors, with leftmost-first
semantics.

It contains a Thompson NFA compiler, a PikeVM, a lazy DFA (forward and
reverse), literal prefilters and a meta searcher that picks a strategy per
pattern, as upstream does.

## Who wrote this

**All of the code, tests and documentation in this repository were written
by an AI** (Anthropic's Claude, run through the ECA editor assistant),
mostly by translating the Rust crates above. No person claims authorship
of any of it. In particular, the person who keeps this repository did not
write it and makes no claim to have written it.

The design and the algorithms are upstream's. The two additions that are
not from upstream, a prefilter on byte sets at fixed distances from a
match's start and skipping the reverse search for fixed-length patterns,
follow ideas from .NET's `System.Text.RegularExpressions`; no .NET code was
used.

## License

Licensed, like upstream, under either of

- the Apache License, Version 2.0 (`LICENSE-APACHE`), or
- the MIT license (`LICENSE-MIT`),

at your option. The copyright notice in those files is upstream's; see
`THIRD-PARTY.md` for where the code comes from. The test data in
`tests/BjoRx.Tests/testdata/` is copied from upstream under the same terms.

## Building and testing

```sh
dotnet build
timeout 180 dotnet run --project tests/BjoRx.Tests --no-build          # all tests
timeout 180 dotnet run --project tests/BjoRx.Tests --no-build -- Meta  # filter by Class.Method
timeout 300 dotnet run -c Release --project bench/BjoRx.Bench          # benchmarks against .NET
```

The fuzz tests run 20 000 cases by default; set `BJORX_FUZZ_CASES` for more.
