package net.oxog.phonebox.deskpanel

import android.util.Base64
import org.json.JSONObject
import java.io.BufferedReader
import java.io.BufferedWriter
import java.io.InputStream
import java.io.InputStreamReader
import java.io.OutputStream
import java.io.OutputStreamWriter
import java.net.InetSocketAddress
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicLong
import javax.net.ssl.SSLSocket

/**
 * Native pinned WebSocket bridge for the Screen widget. Delivers framed screen events to JS on the
 * main thread via [deliver]. Binary media is base64-encoded; JSON events are forwarded as text.
 */
internal class ScreenSocket(
    private val link: AgentLink,
    private val deliver: (Long, String, String?) -> Unit,
) {
    private data class Conn(
        val viewer: Long,
        val widget: String,
        @Volatile var socket: java.net.Socket? = null,
        @Volatile var out: OutputStream? = null,
        @Volatile var closed: Boolean = false,
        val credit: ScreenCredit = ScreenCredit(),
    )

    private val conns = ConcurrentHashMap<Long, Conn>()
    private val random = SecureRandom()
    private val nextId = AtomicLong(1)

    fun open(widget: String, viewer: Long) {
        close(viewer)
        val c = Conn(viewer = viewer, widget = widget)
        conns[viewer] = c
        Thread({ connect(c) }, "screen-ws-$viewer").start()
    }

    fun close(viewer: Long) {
        val c = conns.remove(viewer) ?: return
        c.closed = true
        c.credit.resetViewer(viewer)
        try { c.socket?.close() } catch (_: Exception) {}
    }

    fun closeAll() {
        for (k in conns.keys.toList()) close(k)
    }

    fun send(viewer: Long, message: String) {
        val c = conns[viewer] ?: return
        val o = c.out ?: return
        try {
            writeFrame(o, 0x1, message.toByteArray(Charsets.UTF_8))
        } catch (_: Exception) {
            close(viewer)
        }
    }

    fun acknowledge(viewer: Long, epoch: Long, sequence: Long) {
        val c = conns[viewer] ?: return
        c.credit.acknowledge(viewer, epoch, sequence)
    }

    fun recover(viewer: Long) {
        val c = conns[viewer] ?: return
        val body = """{"op":"recover","viewer":$viewer}""".toByteArray(Charsets.UTF_8)
        val path = "/api/screen?widget=${UriCompat.encode(c.widget)}&viewer=$viewer"
        // best-effort POST through the normal link (pinned, authenticated)
        try {
            val reply = link.request("POST", path, body, "application/json")
            try { reply.body.close() } catch (_: Exception) {}
        } catch (_: Exception) {}
    }

    private fun connect(c: Conn) {
        val attempts = 3
        for (i in 0 until attempts) {
            if (c.closed) return
            try {
                if (tryConnect(c)) return
            } catch (_: Exception) {}
            if (c.closed) return
            try { Thread.sleep((500L * (1L shl i)).coerceAtMost(4000L)) } catch (_: Exception) { return }
        }
        deliver(c.viewer, """{"type":"error","code":"transport"}""", null)
    }

    private fun tryConnect(c: Conn): Boolean {
        // Wi-Fi first when active route is wifi, else try USB then wifi
        val wifi = link.activePc()
        val usbPin = link.usbPinInternal()
        val order = if (link.route == AgentLink.Route.USB) listOf("usb", "wifi") else listOf("wifi", "usb")
        for (mode in order) {
            if (c.closed) return false
            if (mode == "usb" && usbPin != null) {
                if (tryConnectUsb(c, usbPin)) return true
            }
            if (mode == "wifi" && wifi != null) {
                if (tryConnectWifi(c, wifi)) return true
            }
        }
        return false
    }

    private fun tryConnectWifi(c: Conn, pc: AgentLink.Pc): Boolean {
        val f = link.getPinnedFactory(pc.id)
        val path = "/api/screen?widget=${UriCompat.encode(c.widget)}&viewer=${c.viewer}"
        for (host in pc.hosts) {
            if (c.closed) return false
            var raw: java.net.Socket? = null
            try {
                raw = f.createSocket()
                raw.tcpNoDelay = true
                raw.connect(InetSocketAddress(host, pc.port), 2000)
                val s = raw as SSLSocket
                s.soTimeout = 8000
                s.startHandshake()
                if (!wsHandshake(s, host, pc.port, path, "Bearer ${pc.token}", pc.id)) throw java.io.IOException("handshake")
                s.soTimeout = 0
                return runConnection(c, s)
            } catch (_: Exception) {
                try { raw?.close() } catch (_: Exception) {}
            }
        }
        return false
    }

    private fun tryConnectUsb(c: Conn, pin: String): Boolean {
        val f = link.usbPinnedFactoryInternal(pin) ?: return false
        val path = "/api/screen?widget=${UriCompat.encode(c.widget)}&viewer=${c.viewer}"
        var raw: java.net.Socket? = null
        return try {
            raw = f.createSocket()
            raw.tcpNoDelay = true
            raw.connect(InetSocketAddress("127.0.0.1", 8765), 400)
            val s = raw as SSLSocket
            s.soTimeout = 8000
            s.startHandshake()
            val bearer = link.usbBearerInternal() ?: return false
            if (!wsHandshake(s, "127.0.0.1", 8765, path, "Bearer $bearer", pin)) throw java.io.IOException("handshake")
            s.soTimeout = 0
            runConnection(c, s)
        } catch (_: Exception) {
            try { raw?.close() } catch (_: Exception) {}
            false
        }
    }

    private fun wsHandshake(s: SSLSocket, host: String, port: Int, path: String, auth: String, expectedPin: String): Boolean {
        val nonce = ByteArray(16).also { random.nextBytes(it) }
        val secKey = Base64.encodeToString(nonce, Base64.NO_WRAP)
        val sha = MessageDigest.getInstance("SHA-1").digest((secKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").toByteArray(Charsets.US_ASCII))
        val expectedAccept = Base64.encodeToString(sha, Base64.NO_WRAP)

        val writer = BufferedWriter(OutputStreamWriter(s.outputStream, Charsets.US_ASCII))
        writer.write("GET $path HTTP/1.1\r\n")
        writer.write("Host: $host:$port\r\n")
        writer.write("Upgrade: websocket\r\n")
        writer.write("Connection: Upgrade\r\n")
        writer.write("Sec-WebSocket-Key: $secKey\r\n")
        writer.write("Sec-WebSocket-Version: 13\r\n")
        writer.write("Authorization: $auth\r\n\r\n")
        writer.flush()

        val reader = BufferedReader(InputStreamReader(s.inputStream, Charsets.US_ASCII))
        val status = reader.readLine() ?: return false
        if (!status.contains("101")) return false
        var hasAccept = false
        while (true) {
            val line = reader.readLine() ?: break
            if (line.isEmpty()) break
            if (line.startsWith("Sec-WebSocket-Accept:", ignoreCase = true) && line.substringAfter(':').trim() == expectedAccept) hasAccept = true
        }
        return hasAccept
    }

    private fun runConnection(c: Conn, s: SSLSocket): Boolean {
        synchronized(c) {
            if (c.closed) { try { s.close() } catch (_: Exception) {}; return false }
            c.socket = s
            c.out = s.outputStream
        }
        try {
            readLoop(c, s.inputStream)
        } catch (_: Exception) {
        } finally {
            closeSocket(c, s)
        }
        return c.closed // true means intentional close; false means dropped
    }

    private fun closeSocket(c: Conn, s: java.net.Socket) {
        val cur = c.socket
        if (cur === s) {
            c.socket = null
            c.out = null
            try { s.close() } catch (_: Exception) {}
        }
    }

    private fun readLoop(c: Conn, input: InputStream) {
        val maxFrame = 16 * 1024 * 1024
        while (!c.closed) {
            val frame = readWsFrame(input, maxFrame) ?: break
            if (frame.opcode == 0x8) break // close
            if (frame.opcode == 0x9) { // ping -> pong
                try { writeFrame(c.out ?: break, 0xA, frame.payload) } catch (_: Exception) { break }
                continue
            }
            if (frame.opcode != 0x2) continue // expect binary
            handleBinary(c, frame.payload)
        }
    }

    private data class WsFrame(val opcode: Int, val payload: ByteArray)

    private fun readWsFrame(input: InputStream, max: Int): WsFrame? {
        val b0 = input.read(); if (b0 < 0) return null
        val b1 = input.read(); if (b1 < 0) return null
        val opcode = b0 and 0x0F
        var len = (b1 and 0x7F).toLong()
        if (len == 126L) {
            val hi = input.read(); val lo = input.read()
            if (lo < 0) return null
            len = ((hi and 0xFF) shl 8 or (lo and 0xFF)).toLong()
        } else if (len == 127L) {
            var l = 0L
            for (i in 0 until 8) { val x = input.read(); if (x < 0) return null; l = (l shl 8) or (x.toLong() and 0xFF) }
            len = l
        }
        if (len > max) return null
        val payload = ByteArray(len.toInt())
        var off = 0
        while (off < payload.size) {
            val n = input.read(payload, off, payload.size - off)
            if (n < 0) return null
            off += n
        }
        // server-to-client frames are not masked; if masked, unmask
        if ((b1 and 0x80) != 0) {
            val mask = ByteArray(4)
            // Shouldn't happen, but be defensive - mask would have been read as part of length; ignore.
        }
        return WsFrame(opcode, payload)
    }

    private fun handleBinary(c: Conn, payload: ByteArray) {
        if (payload.size < 5) return
        val kind = payload[0].toInt() and 0xFF
        val len = ((payload[1].toInt() and 0xFF) shl 24) or ((payload[2].toInt() and 0xFF) shl 16) or ((payload[3].toInt() and 0xFF) shl 8) or (payload[4].toInt() and 0xFF)
        if (len < 0 || 5 + len > payload.size) return
        val body = payload.copyOfRange(5, 5 + len)
        when (kind) {
            1 -> { // JSON event
                val text = try { String(body, Charsets.UTF_8) } catch (_: Exception) { return }
                deliver(c.viewer, text, null)
            }
            2 -> { // video: [epoch u32][seq u32][mp4]
                if (body.size < 8) return
                val epoch = readU32(body, 0)
                val seq = readU32(body, 4)
                val mp4 = body.copyOfRange(8, body.size)
                if (!c.credit.reserve(c.viewer, epoch, seq, mp4.size)) return
                val b64 = Base64.encodeToString(mp4, Base64.NO_WRAP)
                // Deliver as JSON envelope so JS knows epoch/seq; JS will ack after appendBuffer
                val event = """{"type":"media","epoch":$epoch,"seq":$seq}"""
                deliver(c.viewer, event, b64)
            }
        }
    }

    private fun readU32(b: ByteArray, off: Int): Long {
        return ((b[off].toLong() and 0xFF) shl 24) or ((b[off + 1].toLong() and 0xFF) shl 16) or
            ((b[off + 2].toLong() and 0xFF) shl 8) or (b[off + 3].toLong() and 0xFF)
    }

    private fun writeFrame(out: OutputStream, opcode: Int, payload: ByteArray) {
        val header = ByteArray(6 + (if (payload.size <= 125) 0 else 2))
        header[0] = (0x80 or (opcode and 0x0F)).toByte()
        var idx = 2
        if (payload.size <= 125) {
            header[1] = (0x80 or payload.size).toByte()
        } else {
            header[1] = (0x80 or 126).toByte()
            header[2] = ((payload.size ushr 8) and 0xFF).toByte()
            header[3] = (payload.size and 0xFF).toByte()
            idx = 4
        }
        val mask = ByteArray(4).also { random.nextBytes(it) }
        header[idx] = mask[0]; header[idx+1] = mask[1]; header[idx+2] = mask[2]; header[idx+3] = mask[3]
        out.write(header, 0, idx + 4)
        val masked = ByteArray(payload.size)
        for (i in payload.indices) masked[i] = (payload[i].toInt() xor mask[i % 4].toInt()).toByte()
        out.write(masked)
        out.flush()
    }
}

private object UriCompat {
    fun encode(s: String): String = java.net.URLEncoder.encode(s, "UTF-8").replace("+", "%20")
}
