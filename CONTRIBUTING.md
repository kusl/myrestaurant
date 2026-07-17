# Contributing

Thank you for your interest — but this repository does not accept outside contributions.

This is a single-owner project, published so that anyone may **read, run, fork, and modify their own copy** under the AGPL-3.0-only license. It is not developed in the open in the collaborative sense:

- **Issues are disabled.** There is no bug tracker to file into.
- **Pull requests are closed unreviewed.** GitHub does not allow disabling pull requests on public repositories, so this file is the notice: unsolicited pull requests will be closed without review, whatever their merit. Nothing personal.
- **All copyright remains with the authors.** No contributor license agreement exists because no contributions are accepted.

If you want a change, fork it — the AGPL guarantees you that freedom, and your fork owes its users the same.

## For the owner (and the owner's tooling)

Changes to this repository follow the **atomic documentation** rule (`REQUIREMENTS.md` §10, technical specification §18): a behavior change lands in **one commit** together with its `docs/REQUIREMENTS.md` edit, its `docs/TECHNICAL_SPECIFICATION.md` edit, a `docs/DOCUMENTATION_REVIEW.md` ledger row where a finding is involved, and edits to any affected `docs/adr/` record. ADRs are edited **in place** with a dated History line — never duplicated, never superseded by a new file. No implementation is complete until the code and every document describing it agree.
