/* Event sink for the DLL TLS callback order. The host loads it before the library under test and keeps it loaded, so
 * the events of a detach survive the library that recorded them. Events are recorded one step at a time: the host
 * serializes the threads that record. */
#define SINK_CAPACITY 64U

static unsigned long long sink_events[SINK_CAPACITY];
static unsigned sink_count;

/* An absolute pointer gives the image a relocation, which the guest loader requires of a library it maps away from
 * the preferred base. */
static unsigned long long *const sink_storage = sink_events;

__declspec(dllexport) void SinkRecord(unsigned long long event)
{
    if (sink_count < SINK_CAPACITY) {
        sink_storage[sink_count] = event;
    }
    ++sink_count;
}

__declspec(dllexport) unsigned SinkRead(unsigned long long *events, unsigned capacity)
{
    for (unsigned i = 0; i < sink_count && i < SINK_CAPACITY && i < capacity; ++i) {
        events[i] = sink_storage[i];
    }
    return sink_count;
}
