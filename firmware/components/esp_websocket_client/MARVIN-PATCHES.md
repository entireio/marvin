# Marvin patches to Espressif WebSocket client1.8.0

The upstream license is retained. One local extension adds `tcp_nodelay` to the client configuration and sets `TCP_NODELAY` on the connected transport before the connected event. Default behavior remains unchanged. The setting is requested by Marvin's audio runtime because the ESP-IDF5.4.2 WebSocket transport writes header and payload separately; Nagle/delayed acknowledgments can back up small realtime PCM frames.

The patch uses `esp_transport_get_socket`, a private ESP-IDF5.4.2 accessor also used by its transport tests. Review this boundary on any IDF upgrade. The application already rejects unreviewed IDF>=5.5.1 builds because of WebSocket redirect behavior. A failed socket-option request fails the connection; it never relaxes TLS verification. Physical throughput evidence is required before accepting this change as an audio fix.
