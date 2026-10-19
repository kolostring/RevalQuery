# A query's saves go through one ordered queue, whether a fetch or a set wrote them

Status: Accepted
Date: 2026-10-19

## Context
`query-data-is-set-through-the-client` queues a set's saves per query, so the last set is the
last saved. A fetch still saves outside that queue, in `QueryWorker`. A fetch that started
before a set and lands after it wins in memory, which that ADR accepts. But its save and the
set's save can reach the store in either order, so after a restart the store can bring back a
value the registry had already replaced.

## Decision
- Each query has one save queue. A fetch's save and a set's save both enqueue on it, in the
  order their writes were applied to the query, with the enqueue taken in the same step as the
  write.
- The store therefore ends holding the value the registry ended holding.
- A failed save is swallowed and the queue moves on, for both kinds.

## Considered options
- **Order only the sets, and document fetch and set saves as unordered**: rejected. Memory and
  the store could disagree after a restart, which is the reverted-data symptom that ordered set
  saves exist to prevent.
- **Have a set cancel or supersede an in-flight fetch**: rejected. It contradicts
  `cancelling-a-fetch-and-abandoning-a-wait-are-different-operations` and the set ADR: a set
  never cancels.

## Consequences
- A slow save delays every later save of the same query, never another query's.
- Future code that persists a query's data enqueues on this queue.
