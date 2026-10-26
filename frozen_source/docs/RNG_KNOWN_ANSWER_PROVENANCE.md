# RNG known-answer provenance

The ER-05 known-answer vector uses master seed `0x0123456789ABCDEF`, experiment `SEM-002`, scenario `FX-RNG-KAT`, trajectory `TRJ-0001`, episode `EP-0001`, decision `DEC-0001`, coupling group `CRN-0001`, purpose `DegradationInnovation` (`3`), and substream `7`.

The expected canonical bytes, SHA-256 digest, four-word xoshiro256** state, first five `ulong` outputs, and first upper-53-bit `[0,1)` conversion are hard-coded in `RandomnessKnownAnswerTests`. They were independently derived with a separate PowerShell/`System.Numerics.BigInteger` implementation of unsigned 64-bit rotate/multiply behavior. The independent derivation did not call the C# RNG under test. Runtime tests compare the C# serializer, digest/state decoding, generator sequence, and IEEE-754 conversion against those immutable values.

This fixture validates software reproducibility only; it is not a scientific result and does not authorize T5 or T6.
