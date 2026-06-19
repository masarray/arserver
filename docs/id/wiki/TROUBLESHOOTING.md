# Troubleshooting

## IED tidak connect

Cek item berikut terlebih dahulu:

- Alamat IP benar.
- MMS port benar, umumnya `102`.
- PC dan IED berada pada jaringan yang bisa saling routing.
- Windows Firewall mengizinkan akses keluar ARServer dan akses masuk Modbus jika digunakan.
- Service MMS pada IED aktif.
- Tool engineering lain tidak menghabiskan session client yang tersedia.
- VLAN, gateway, subnet mask, dan switch port benar.

## Discovery connect tetapi signal sedikit atau kosong

Kemungkinan penyebab:

- IED membatasi online model browsing.
- Access point yang dipilih bukan access point MMS.
- Model IED memakai bentuk object name yang spesifik vendor.
- Relay mengizinkan read object tertentu tetapi membatasi directory browsing.

Aksi yang disarankan:

- Coba **Open SCL/CID/SCD** jika file tersedia.
- Cek diagnostics untuk pesan browse domain dan variable.
- Probe CB position atau measurement yang sudah diketahui dari SCL.

## Probe selected gagal

Probe memvalidasi final object path dan functional constraint. Kegagalan bisa berarti:

- object path berbeda dari kandidat discovery;
- functional constraint salah;
- relay tidak mengizinkan pembacaan atribut tersebut;
- point tidak ada pada varian IED ini;
- koneksi ditutup oleh IED setelah request tidak didukung.

Coba kandidat lain dari logical node yang sama, atau gunakan import SCL agar object reference lebih deterministic.

## Value live tetapi timestamp kosong

Atribut value mungkin bisa dibaca, tetapi companion attribute `t` untuk timestamp tidak tersedia atau tidak diizinkan. ARServer membiarkan timestamp kosong, bukan membuat device timestamp palsu.

## Value live tetapi quality kosong atau Bad

Quality berasal dari companion attribute `q` jika tersedia. Jika relay mengembalikan quality invalid atau unavailable, ARServer menampilkan state tersebut.

## Modbus client connect tetapi value tidak berubah

Cek:

- Runtime sedang berjalan.
- IEC value live di runtime grid.
- Binding aktif.
- Alamat register Modbus benar.
- Client memakai Unit ID yang benar.
- Client memakai konvensi address zero-based atau one-based yang sesuai mapping.
- Client membaca ukuran tipe data yang benar.

## MQTT tidak publish

Cek:

- MQTT aktif secara global.
- MQTT aktif pada binding yang dipilih.
- Broker host dan port benar.
- Broker menerima anonymous access atau credential yang dikonfigurasi.
- Network/firewall mengizinkan akses ke broker.
- Tab diagnostics menunjukkan MQTT connected.

## Runtime diblokir

ARServer memblokir runtime saat tidak bisa membuat IEC live session yang aman. Ini mencegah publikasi value stale atau value karangan.

Perbaiki koneksi IEC terlebih dahulu, lalu start runtime lagi.

## Kapan memakai SCL import

Gunakan SCL/CID/SCD/ICD jika:

- IP discovery menghasilkan terlalu banyak kandidat;
- online browse dibatasi;
- perlu planning DataSet atau RCB;
- ingin mapping engineering yang repeatable;
- struktur object relay sudah diketahui dari project file.

## Informasi yang perlu disertakan saat melaporkan bug

Sertakan:

- versi ARServer;
- versi Windows;
- vendor/model/firmware relay jika boleh dibagikan;
- workflow yang dipakai: Add by IP atau Open SCL;
- screenshot diagnostics;
- IEC object yang dipilih;
- expected value dan observed value;
- setting Modbus/MQTT jika masalah terkait output.
