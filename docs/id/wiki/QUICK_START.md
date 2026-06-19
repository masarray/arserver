# Quick Start ARServer

Panduan ini membawa user dari IP relay atau file SCL sampai gateway Modbus TCP/MQTT berjalan.

## 1. Buka aplikasi

Buka `ArServer.exe` atau jalankan project dari Visual Studio 2022.

Aplikasi akan terbuka pada **IEC 61850 Explorer**. Runtime masih berhenti sampai tombol **Start** ditekan.

## 2. Tambah IED berdasarkan IP

Gunakan cara ini jika relay/IED live bisa dijangkau dari jaringan.

1. Klik **+ Add IED**.
2. Pilih **Add by IP**.
3. Masukkan alamat IP IED.
4. Biarkan MMS port `102`, kecuali relay memakai port lain.
5. Klik **Connect & Discover**.
6. Review kandidat signal IEC 61850 yang ditemukan.
7. Pilih SCADA point yang dibutuhkan.
8. Klik **Probe Selected** untuk memastikan point bisa dibaca.
9. Tentukan alamat register Modbus dan routing MQTT.
10. Klik **Add to Runtime**.

## 3. Tambah IED dari file SCL/CID/SCD/ICD

Gunakan cara ini jika file engineering tersedia.

1. Klik **+ Add IED**.
2. Pilih **Open SCL**.
3. Pilih file engineering.
4. Review IED name, IP, DataSet, dan kandidat ReportControl yang terdeteksi.
5. Override runtime IP jika IP relay aktual berbeda dari file.
6. Pilih SCADA tag.
7. Probe tag terpilih jika relay dapat dijangkau.
8. Tentukan routing Modbus/MQTT.
9. Klik **Add to Runtime**.

## 4. Cek runtime grid

Runtime grid disusun seperti ini:

```text
IEC Object | Value | Timestamp | Quality | Type
```

Status point yang sehat biasanya menampilkan:

- IEC object seperti `LD0/XCBR1.Pos.stVal`;
- value seperti `Open` atau `Closed`;
- device timestamp jika atribut `t` bisa dibaca;
- quality jika atribut `q` bisa dibaca;
- type seperti `Dbpos`.

## 5. Jalankan runtime

1. Pastikan minimal satu binding sudah ada.
2. Atur MMS polling interval.
3. Aktifkan atau nonaktifkan Fast CB lane sesuai kebutuhan.
4. Tekan **Start**.

Tab diagnostics akan menampilkan pesan IEC 61850, Modbus, MQTT, dan runtime.

## 6. Hubungkan Modbus TCP client

Default server:

```text
Address: IP PC yang menjalankan ARServer
Port: 502
Unit ID: 1
```

Untuk FUXA atau HMI lain:

1. Buat koneksi Modbus TCP.
2. Masukkan IP PC ARServer.
3. Gunakan port dan Unit ID yang dikonfigurasi.
4. Tambahkan tag memakai alamat register yang sudah dimapping.

## 7. Aktifkan MQTT

1. Buka tab MQTT.
2. Aktifkan MQTT.
3. Isi broker host, port, dan topic root.
4. Aktifkan MQTT untuk binding yang dipilih.
5. Jalankan runtime.
6. Subscribe dari dashboard, broker test, atau data collector.

## 8. Simpan project

Gunakan **Save Project** setelah mapping berhasil. Project menyimpan endpoint IED, IEC object terpilih, mapping Modbus, pengaturan MQTT, dan opsi runtime.

## Checklist cepat

- IED bisa dijangkau melalui ping atau jaringan routing yang sama.
- MMS TCP port bisa dijangkau.
- Discovery mengembalikan kandidat signal.
- Probe selected signal berhasil.
- Runtime grid menampilkan value.
- Binding Modbus aktif.
- Runtime berjalan.
- HMI konek ke ARServer, bukan langsung ke relay.
