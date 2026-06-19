# Roadmap ARServer

ARServer bergerak menuju workflow gateway IEC 61850 native yang lengkap untuk kebutuhan HMI, SCADA, Modbus TCP, dan MQTT secara praktis.

## Arah desain

Arah produknya sederhana:

```text
IED / Relay → native IEC 61850 MMS client → runtime cache → Modbus TCP / MQTT
```

HMI atau SCADA tidak seharusnya polling relay langsung untuk setiap refresh layar. ARServer menjadi layer gateway dengan selected signals, mapping yang jelas, cached value, device timestamp, quality, dan output diagnostics.

## Milestone yang sudah diimplementasikan

### N1 — Native transport foundation

- Koneksi TCP port 102.
- TPKT frame handling.
- COTP connection request/confirm.
- Runtime status dipisahkan antara transport dan application association.

### N2 — ACSE/MMS association

- ISO session dan presentation handshake.
- ACSE association request.
- MMS initiate request/response probe.
- Diagnostics untuk association state.

### N3/N4 — Confirmed-Read

- Single-variable MMS Confirmed-Read.
- Mapping IEC object ke MMS domain/item.
- Validasi response invoke ID.
- Decoder awal untuk status, Boolean, integer, float, string, quality, dan timestamp-like value.

### N7 — Correct Presentation P-DATA envelope

- Confirmed-Read dibungkus dengan Presentation P-DATA.
- Response unwrap sebelum MMS decode.
- Read path yang sudah terbukti untuk CB position value.

### N8 — Native IP discovery

- Online MMS discovery by IP.
- Domain browse.
- Domain variable browse.
- Mapping MMS name ke kandidat IEC object.
- Rekomendasi kandidat yang lebih siap untuk SCADA.

### N9 — Quality dan timestamp sidecar

- Companion read `q` dan `t` jika tersedia.
- Runtime snapshot membawa local timestamp dan device timestamp secara terpisah.
- MQTT payload menyertakan value, quality, local timestamp, dan device timestamp.

### N10 — Probe before runtime commit

- Probe selected signal pada level wizard.
- Probe memvalidasi value dan mencoba companion quality/timestamp read.
- Runtime grid disusun sebagai `IEC Object | Value | Timestamp | Quality | Type`.

### N11R — Report plan di Edit IED Wizard

- Inventory RCB/DataSet ditampilkan di Edit IED Wizard sebagai langkah konfigurasi.
- IP discovery tidak auto-probe RCB attribute agar discovery tetap stabil.
- RCB attribute probe bersifat eksplisit dan read-only setelah dipilih user.
- Runtime tetap MMS polling sampai report activation diimplementasikan.

## Milestone berikutnya

### N12 — DataSet directory engine

- Read DataSet member directory untuk RCB/DataSet terpilih.
- Tampilkan coverage antara DataSet member dan selected runtime signal.
- Report activation tetap disabled sampai receive loop dan safe RCB enable sequence siap.

### N13 — Discovery hardening

- Filtering lebih baik untuk LN class umum.
- Handling lebih baik untuk MMS name spesifik vendor.
- Type inference lebih deterministic.
- Discovery report export.

### N14 — Multi-point read optimization

- Group read berdasarkan relay dan functional constraint.
- Mengurangi jumlah request untuk mapping besar.
- Menjaga fast lane untuk CB/status/protection point.
- Menambahkan timeout dan retry profile per IED.

### N15 — Report verification

- Online DataSet browse.
- Online ReportControl browse.
- Bandingkan selected signal dengan DataSet member.
- Tampilkan ownership/readiness RCB sebelum activation.

### N16 — Report activation dengan polling fallback

- Enable report-preferred runtime mode.
- Decode InformationReport values.
- Pertahankan polling fallback untuk report yang stale atau gagal.
- Tampilkan report state di diagnostics.

### N17 — Mapping/report documentation

- Export Modbus register map.
- Export selected IEC object list.
- Export validation summary.
- Buat FAT-friendly evidence report.

## Prinsip produk

- Read-only operation lebih dulu.
- Jangan pernah mengarang field value.
- Pisahkan device timestamp dari local PC timestamp.
- Buat mapping Modbus dan MQTT eksplisit.
- Prefer SCL saat file engineering tersedia.
- Gunakan IP discovery untuk quick online setup.
- Jaga diagnostics tetap berguna untuk troubleshooting lapangan.
- Repository tetap Apache-2.0 dan self-contained.
