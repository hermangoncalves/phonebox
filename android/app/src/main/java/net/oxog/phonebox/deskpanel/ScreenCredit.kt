package net.oxog.phonebox.deskpanel

/**
 * Byte credit for the native screen bridge. The agent sends at most [limit] bytes of unacknowledged
 * binary media; an acknowledgement for a matching viewer/epoch/sequence releases that credit.
 * Stale viewers, epochs and sequences never release credit belonging to a newer stream.
 */
internal class ScreenCredit(private val limit: Int = 512 * 1024) {
    private data class Pending(val viewer: Long, val epoch: Long, val sequence: Long, val bytes: Int)

    private val pending = mutableListOf<Pending>()
    private var used: Int = 0

    val pendingBytes: Int get() = used

    fun reserve(viewer: Long, epoch: Long, sequence: Long, bytes: Int): Boolean {
        if (bytes < 0) return false
        if (bytes > limit) return false
        if (pending.any { it.viewer == viewer && it.epoch == epoch && it.sequence == sequence }) return false
        if (used + bytes > limit) return false
        pending.add(Pending(viewer, epoch, sequence, bytes))
        used += bytes
        return true
    }

    fun acknowledge(viewer: Long, epoch: Long, sequence: Long): Boolean {
        val idx = pending.indexOfFirst { it.viewer == viewer && it.epoch == epoch && it.sequence == sequence }
        if (idx < 0) return false
        val p = pending.removeAt(idx)
        used = (used - p.bytes).coerceAtLeast(0)
        return true
    }

    fun reset(viewer: Long, epoch: Long) {
        val toRemove = pending.filter { it.viewer == viewer && it.epoch == epoch }
        toRemove.forEach { pending.remove(it); used = (used - it.bytes).coerceAtLeast(0) }
    }

    fun resetViewer(viewer: Long) {
        val toRemove = pending.filter { it.viewer == viewer }
        toRemove.forEach { pending.remove(it); used = (used - it.bytes).coerceAtLeast(0) }
    }
}
