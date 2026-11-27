package net.oxog.phonebox.deskpanel

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ScreenCreditTest {
    @Test fun staleAcknowledgementCannotReleaseNewViewerCredit() {
        val c = ScreenCredit(16)
        c.reset(2, 1)
        assertTrue(c.reserve(2, 1, 0, 12))
        assertFalse(c.acknowledge(1, 1, 0))
        assertEquals(12, c.pendingBytes)
        assertFalse(c.reserve(2, 1, 1, 8))
    }

    @Test fun duplicateSequenceRejected() {
        val c = ScreenCredit(16)
        assertTrue(c.reserve(1, 1, 0, 4))
        assertFalse(c.reserve(1, 1, 0, 4))
    }

    @Test fun acknowledgeReleasesCredit() {
        val c = ScreenCredit(16)
        assertTrue(c.reserve(1, 1, 0, 4))
        assertTrue(c.acknowledge(1, 1, 0))
        assertEquals(0, c.pendingBytes)
        assertTrue(c.reserve(1, 1, 1, 16))
    }

    @Test fun oldEpochAckIgnored() {
        val c = ScreenCredit(16)
        c.reset(1, 2)
        assertTrue(c.reserve(1, 2, 0, 8))
        assertFalse(c.acknowledge(1, 1, 0))
        assertEquals(8, c.pendingBytes)
    }

    @Test fun oversizedChunkRejected() {
        val c = ScreenCredit(16)
        assertFalse(c.reserve(1, 1, 0, 32))
        assertEquals(0, c.pendingBytes)
    }

    @Test fun resetClearsMatchingEpoch() {
        val c = ScreenCredit(16)
        assertTrue(c.reserve(1, 1, 0, 8))
        c.reset(1, 1)
        assertEquals(0, c.pendingBytes)
        assertTrue(c.reserve(1, 1, 0, 8))
    }

    @Test fun resetWhilePendingDoesNotReleaseOtherViewer() {
        val c = ScreenCredit(16)
        assertTrue(c.reserve(1, 1, 0, 8))
        assertTrue(c.reserve(2, 1, 0, 4))
        c.reset(1, 1)
        assertEquals(4, c.pendingBytes)
    }
}
