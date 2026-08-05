# Contributing

Thank you for your interest — but this repository does not accept outside contributions.

This is a single-owner project, published so that anyone may **read, run, fork, and modify their own copy** under the AGPL-3.0-only license. It is not developed in the open in the collaborative sense:

- **There is no bug tracker.** Nothing filed here is triaged, and nothing filed here is a queue anybody works. If the issue tab is open on the day you read this, that is a fallback for the one case described in `SECURITY.md`, not an invitation.
- **Pull requests are closed unreviewed.** GitHub does not allow refusing them outright on a public repository, so this file is the notice: unsolicited pull requests will be closed without review, whatever their merit. Nothing personal.
- **All copyright remains with the authors.** No contributor license agreement exists because no contributions are accepted.

If you want a change, fork it — the AGPL guarantees you that freedom, and your fork owes its users the same.

## The one exception: security

**A vulnerability report is not a contribution, and `SECURITY.md` is the door for it.**

The distinction is not a courtesy. Refusing a feature costs the person who wanted the feature, and they have the source and the freedom to build it themselves — that is the arrangement, and it is a fair one. Refusing a report about a forgeable capability token costs somebody's *guests*, who never chose this software, cannot read this file, and have no fork to run. So the two things get opposite answers.

Read `SECURITY.md` before reporting: it names the private channel, and it points at the accepted-risks register (`docs/TECHNICAL_SPECIFICATION.md` §17) so that a decision already argued and written down does not cost you an evening. **Do not send the fix as a pull request** — that publishes the defect in a diff before there is a release to upgrade to.

## A note on what this file may claim

This document used to say that the issue tracker had been switched off. It had not been, and had never been — the setting was on for as long as the sentence existed, so the only document addressing the question was wrong about it, and the sentence stood because nothing in a repository can inspect its own settings page. That is finding **F-42**.

The rule that came out of it, enforced by `scripts/check_repository.sh` rather than remembered: **a document in this tree states policy, never platform state.** "Nothing filed here is triaged" is true wherever it is read and survives somebody toggling a checkbox. A claim about the checkbox is true only until the checkbox moves, and no gate can tell you when that happened.

## For the owner (and the owner's tooling)

Changes to this repository follow the **atomic documentation** rule (`REQUIREMENTS.md` §10, technical specification §18): a behavior change lands in **one commit** together with its `docs/REQUIREMENTS.md` edit, its `docs/TECHNICAL_SPECIFICATION.md` edit, a `docs/DOCUMENTATION_REVIEW.md` ledger row where a finding is involved, and edits to any affected `docs/adr/` record. ADRs are edited **in place** with a dated History line — never duplicated, never superseded by a new file. No implementation is complete until the code and every document describing it agree.
