---
status: accepted
---

# Query key segments are compared by value, never hashed or stringified

Query identity is resolved by comparing key segments as objects, using the registry's
child dictionary keyed on the boxed segment itself. We do not hash segments to an
integer, and we do not convert them to strings. This looks like something to optimise
and is not; both alternatives are silently wrong.

Hashing to 32 bits collides. We found a real collision between two distinct string keys
in a single process, where the second key resolved to the first key's state and returned
another query's data with no error. Widening to 64 bits makes that unlikely rather than
impossible, and a per-process randomised hash cannot survive a restart, which persistence
needs.

Stringifying with `ToString()` is worse, because it is wrong deterministically rather
than rarely. `("users", 1)` and `("users", "1")` produce the same segment text and
collapse into one query. Numeric and date formatting is culture-sensitive, so the same
key produces different entries under a different locale.

## Considered options

Measured over 2,000,000 lookups of a three-segment key, in Release:

| strategy | time | allocation | correct |
|---|---|---|---|
| `ToString()` into a string-keyed dictionary | 104 ms | 24 B | no |
| invariant formatting with a type tag | 233 ms | 160 B | yes |
| `System.Text.Json` per segment | 854 ms | 136 B | yes |
| boxed segment, default comparer | 191 ms | 24 B | yes |

The chosen option is the fastest correct one and allocates the least, because its 24
bytes are `ITuple`'s own boxing, which every strategy pays. The `ToString()` call the
old code made was pure added cost on top of being wrong.

No custom comparer is needed. `Int32.Equals(object)` already returns false for a string,
so `1` and `"1"` separate for free, and comparing values rather than formatted text makes
culture irrelevant by construction.

The absolute numbers say this was never worth optimising: roughly 32ns per segment, about
95ns to resolve a three-segment key.

## Consequences

Key segments must implement value equality. Primitives, strings, enums, records and value
tuples all do. A reference type with default identity equality will not match across
calls.

`Dictionary` rejects null keys, so a null segment maps to a private sentinel.

Persistence needs its own key encoding, because boxed objects cannot be written to
external storage. That encoding is off the hot path, so JSON per segment is appropriate
there, and it is a second representation that must stay consistent with this one.
