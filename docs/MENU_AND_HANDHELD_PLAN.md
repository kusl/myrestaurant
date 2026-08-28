# Menu modernization and the handheld contract — staged plan

**Opened 2026-08-11, at the close of M6 Slice 30.** The execution plan for the first enhancement request this project received from a person who was shown the running application, together with the defect that request arrived beside.

**This file is now a status register.** The stage-by-stage argument — what each stage was cut where it was cut, the rulings inside it, what it deliberately did not build, and what was open after it — is in [`docs/progress/MENU_AND_HANDHELD_PLAN_THROUGH_STAGE_1D.md`](progress/MENU_AND_HANDHELD_PLAN_THROUGH_STAGE_1D.md), withheld from the context dump and still tracked. What survives here is the table and the rulings that outlive the stages they came from.

## What was asked for

A menu with headings, a description under each dish, a photograph beside it, and somewhere for a guest to say what they thought — asked for by somebody holding a phone, standing up, in a dining room. **The handheld contract came first** and that ordering was the first ruling: a menu rebuilt on a layout that assumes a desktop has to be rebuilt again.

## Stage status

| Stage | What it is | Status |
|---|---|---|
| **1** | the handheld contract | open (its sub-stages are complete) |
| **1a** | the vocabulary and the four index pages | landed, M6 Slice 30 |
| **1b** | the remaining surfaces | administration half landed, M6 Slice 34 |
| **1c** | an end-to-end barrier at 375px | landed, M6 Slice 32 |
| **1d** | the guest's own menu, at the width it is read at | landed, M6 Slice 64 |
| **1e** | the picture on a card, at the width it is read at | landed, M6 Slice 65 |
| **2** | sections and descriptions: schema and data access | closed, M6 Slice 40 |
| **3** | sections and descriptions: the surfaces | landed across 39, 47, 48, 50, 61 |
| **3a** | the resequencing verb | landed, M6 Slice 47 |
| **3b** | the item resequencing verb | landed, M6 Slice 48 |
| **3c** | the kitchen's 86 panel | landed, M6 Slice 50 |
| **3d** | a scenario that presses the resequencing controls | landed, M6 Slice 61 |
| **4a** | images: the schema and the data access | landed, M6 Slice 51 |
| **4b** | images: the route and the administrator's form | landed, M6 Slice 52 |
| **4c** | images: the guest's menu | landed, M6 Slice 53 |
| **4d** | images: the picture's history | landed, M6 Slice 54 |
| **4e** | images: a picture a phone can actually upload | landed, M6 Slice 55 |
| **4f** | images: the bytes decide the format | landed, M6 Slice 56 |
| **5a** | likes: the schema and the data access | landed, M6 Slice 57 |
| **5b** | likes: the guest's control | landed, M6 Slice 58 |
| **5c** | likes: a dish that is off tonight | landed, M6 Slice 60 |
| **6** | guest comments | closed, M6 Slice 72 |
| **6a** | the rate limiter takes a second policy | landed, M6 Slice 62 (prerequisite 1) |
| **6b** | the rendering rule stops being a sentence | landed, M6 Slice 63 (prerequisite 2) |
| **6c** | comments: the schema and the data access | landed, M6 Slice 68 |
| **6d** | comments: the guest's control | landed, M6 Slice 71 |
| **6e** | comments: the staff read | landed, M6 Slice 72 |

## The rulings that outlive their stages

These are the parts of the plan worth keeping after the stage landed. Each is embodied in `docs/TECHNICAL_SPECIFICATION.md`; the section is named so this file states no mechanism of its own.

| Ruling | Where it lives |
|---|---|
| Every item is filed under exactly one heading; there is no unsectioned item | §7, §8.2 |
| A verb that would store the value already there writes nothing and returns `NoChange` | §7 |
| Only activation and deactivation publish `MenuChanged`; rename and describe do not | §7, §9 |
| Every menu read's order ends in the identifier, because a tie broken by anything else is broken differently on two reads | §7 |
| One picture per item, enforced by the primary key rather than by a query | §7, §8.2 |
| The bytes decide the media type, not the declaration | §7, ADR-0015 |
| `alt` is always emitted and `''` is not the same as absent | §7, §11.1 |
| The client only ever makes a payload smaller; every refusal is the server's | §7 |
| A like is an opinion, not a record of having eaten — no prior order is required | §7 |
| The like count is staff-facing; a guest sees only their own press | §7, §11.1, §11.4 |
| The like control is in the item's detail panel, never on its card | §11.1 |
| A reaction publishes nothing, because the menu has not changed | §7, §9 |
| The kitchen's 86 panel obeys the opposite of §11.1's rule, and that is required rather than permitted | §11.2 |
| A comment is filed against the item and never against an order line | §7 |
| A comment is staff-facing; a guest sees only their own | §7 |
| One standing comment per person per dish; editing is resubmission and every version is kept | §7, §8.2 |
| A withdrawn comment stops being rendered and stays in the log | §7, §8.3 |
| The comment length cap is the schema's and is stated once | §7, §8.2 |
| The comment box is in the item's detail panel, never on its card — a textarea inside a button is markup a parser takes apart | §7, §11.1 |
| A blank body is a refusal and never a withdrawal; the refusal names the control that does withdraw | §7, §11.1 |
| The client's cap is an optimisation and every refusal is the server's, exactly as for a picture's bytes | §7, §11.1 |
| The draft belongs to the chosen dish, and a menu re-read never overwrites what somebody is typing | §11.1 |
| The surface declares the outcome beside the sentence, so a barrier never asserts the copywriting | §11.1, §16.3 |
| The staff read is the whole-menu read; a dish's own page carries no list of its own | §7, §11.4 |
| The comment block is grouped by dish in the menu's own order, because the read's own order is a UUID ordering | §7, §11.4 |
| A count chip is absent rather than zero where nobody has spoken | §7, §11.4 |

## Stage 6 — guest comments, and what is settled

**Two prerequisites were discharged ahead of it**, each in its own slice, because both were wanted regardless of whether comments ever shipped.

- **Prerequisite 1 — a refusal an endpoint decides (Slice 62).** `/register` had been documented as rate-limited for eleven slices without being rate-limited. The limiter now takes a second policy and refuses at the endpoint rather than on a page, so a comment surface has somewhere to attach.
- **Prerequisite 2 — the rendering rule stops being a sentence (Slice 63).** Guest-authored text is the first content in this application written by somebody who is not staff, so "it is rendered as text and never as markup" had to become a gate rather than a paragraph. `RawHtmlContractTests` asserts that raw HTML has a closed set of sources and that none of them is a person.

**The four open questions, and how Slice 68 settled three of them.** Each is decided in §7 and the reason is there rather than here.

| Question | Ruling | Why |
|---|---|---|
| Attached to an item or to an order line? | the item | An opinion needs no purchase, which the like already settled; and an order line's log carries §6.7's correction rules a comment has no business inheriting. |
| Who may read it, and for how long? | staff, and for as long as the log | §11.4's like-count ruling applied to text. Retention is the order log's, because inventing one policy for one table is how two policies start. |
| May staff reply? | not built | A reply makes a thread, a thread makes a conversation, and a conversation needs a role, a notification and a moderation rule. Deferred, and named as deferred. |
| What does moderation mean for an append-only log? | the question does not arise | Nothing a guest writes is rendered to another guest, so there is nobody to moderate on behalf of. Stage 6d shipped a guest their own comment and nobody else's, so the question still does not arise; should any later stage ever show one guest another's words, this row is what has to be reopened first. |

**What is left: nothing in this plan.** Stage 6e landed in Slice 72 with its four rulings in §7, its block and chip on §11.4's menu index, and six end-to-end claims on §16.3 scenario 21's arrangement. Every stage this plan opened on 2026-08-11 is now closed, and the enhancement request that produced it — headings, a description, a photograph, and somewhere to say what you thought — is answered end to end. The moderation row above stays as written: the staff read shows staff one guest's words and shows no guest another's, so the question still does not arise, and that row is what has to be reopened before any surface changes it. The fourth Stage 6 question — staff replies — is still deliberately not built.

**Stage 6c was executed for the first time in Slice 69**, and the schema it declares is correct: the five things `menu_item_comment_event` refuses, it refuses by the name the writer recognises. What was wrong was one probe in the test, which offered a row breaking two CHECKs at once and asserted the name of the one PostgreSQL happens not to report (**F-123**). No ruling in the table above moved.
