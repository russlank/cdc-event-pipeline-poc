# Breaking Change Example

`PayEvent.avsc` here is contract v2 with one change: in `SalaryChangedPayload`
the field `monthlyAmount` is renamed to `amount`. It must never be registered;
`ci/schema-compat.sh` checks on every run that the registry rejects it.

Why it breaks consumers, in both directions that `FULL_TRANSITIVE` protects:

- **New readers, old data (backward):** a consumer built with this schema
  reads an event written with v1 or v2. The data has no `amount`, and `amount`
  has no default, so the reader cannot fill it in. Resolution fails.
- **Old readers, new data (forward):** a v1 or v2 consumer reads an event
  written with this schema. It expects `monthlyAmount`, which the data does not
  contain and which has no default in the reader. Resolution fails; a consumer
  that ignored the error would lose the salary amount.

A rename in Avro is a removal plus an addition. Done safely it takes several
compatible steps: add `amount` with a default, write both fields, move readers,
then stop using the old one; or publish a new topic version
(`hr.employee-pay.v2`) for a clean break.
