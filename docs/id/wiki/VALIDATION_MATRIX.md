# Validation Matrix

Gunakan matrix ini saat mengevaluasi ARServer di lab bench, FAT bench, atau environment simulasi.

| Area | Yang divalidasi | Hasil yang diharapkan |
|---|---|---|
| Application start | Portable package dibuka di Windows | Main workspace terbuka tanpa Visual Studio |
| Demo mode | Tambah sample IED dan pilih signal | Value update dan bisa dimapping |
| IED connection | Connect ke test relay pada MMS port | Status koneksi dan discovery terlihat |
| SCL import | Import file CID/SCD/SCL | Signal yang siap untuk SCADA muncul di wizard |
| IEC Reference visibility | Buka signal selection wizard | IEC Reference terlihat dekat kolom Signal |
| Modbus map | Bind value terpilih ke address | Tidak ada address overlap untuk point aktif |
| Modbus read | Read dari external Modbus master | Value sama dengan runtime cache ARServer |
| Float32 handling | Read analog value dari HMI | Word order dan scale benar |
| MQTT publish | Subscribe ke topic yang dikonfigurasi | Topic value, quality, status, dan state update |
| Fast CB | Aktifkan Fast CB dengan sedikit status point | Status point diprioritaskan sebelum analog point |
| Stale handling | Disconnect IED saat runtime | Indikasi quality/stale berubah secara terlihat |
| Restart behavior | Save project, close, reopen | IED workspace dan mapping tersimpan |
| Safety | Coba Modbus write | Write ditolak by design |

## Rekomendasi bench

Mulai dari selected set kecil:

- satu breaker position;
- satu trip/start flag;
- satu analog measurement;
- satu quality/status point.

Setelah itu naikkan jumlah point secara bertahap sambil memantau refresh behavior, stale indication, dan beban CPU/network.
