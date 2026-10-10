using Xunit;

// xunit's default limits parallel tests to one per CPU by running everything on that many threads
// through a synchronization context. The API runs in the test process and its awaits capture
// that context, so on a small machine (CI has 4 CPUs) the API's own continuations queue behind
// test code. A request holding the admin-membership lock inside a transaction then stalls between
// statements for as long as the queue is busy and every other request times out on the lock
// (503s). -1 turns the limit, and with it the context, off.
[assembly: CollectionBehavior(MaxParallelThreads = -1)]
