---

# **PLC Data Logger - Sistem Pencatat Produksi Otomatis**

Program ini adalah aplikasi Windows yang digunakan untuk membaca data dari mesin PLC (Programmable Logic Controller) secara otomatis dan mencatat hasil produksi ke dalam file laporan. Sistem ini dirancang untuk membantu pabrik atau industri mencatat model produk yang diproduksi tanpa perlu input manual. Data yang masuk dari PLC akan langsung tercatat dengan waktu yang tepat, sehingga memudahkan monitoring dan pelaporan harian.

## **File-File Penting dalam Program**

Program ini secara otomatis membuat dan mengelola beberapa file di folder yang sama dengan aplikasi. Berikut penjelasan masing-masing file:

- **`config.txt`** - File ini menyimpan pengaturan koneksi ke PLC seperti alamat IP, nomor port, dan lokasi folder penyimpanan laporan. File ini dibuat otomatis saat pertama kali menjalankan program dan akan terupdate setiap kali Anda mengubah pengaturan.

- **`error_log.txt`** - File ini mencatat semua masalah atau error yang terjadi saat program berjalan, seperti gagal koneksi ke PLC atau masalah saat menyimpan data. Anda bisa membuka file ini untuk melihat riwayat masalah sistem.

- **`Produksi_DD-MM-YYYY.csv`** - Ini adalah laporan harian yang dibuat otomatis setiap hari. Setiap baris berisi waktu pencatatan dan nama model produk yang masuk dari PLC. File ini bisa dibuka langsung dengan Microsoft Excel.

---

## **Cara Menggunakan Program**

1. **Menjalankan Program**
   - Klik dua kali file aplikasi untuk membukanya
   - Pastikan hanya satu aplikasi yang berjalan (program akan menolak jika dibuka dua kali)

2. **Mengatur Koneksi ke PLC**
   - Masukkan alamat IP PLC di kolom "IP Address" (contoh: 192.168.1.10)
   - Masukkan nomor port (biasanya 8501 untuk PLC Keyence)
   - Masukkan alamat memori PLC (contoh: DM600)
   - Pengaturan akan tersimpan otomatis saat Anda klik di luar kolom input

3. **Memilih Folder Penyimpanan Laporan**
   - Klik tombol "Set Folder" untuk memilih lokasi folder tempat menyimpan file laporan harian
   - Pilih folder yang mudah diakses, misalnya di Desktop atau Documents
   - Folder ini juga akan tersimpan di `config.txt`

4. **Monitoring Data**
   - Status koneksi ditampilkan di bagian bawah: hijau untuk "ONLINE" dan merah untuk "OFFLINE"
   - Data yang masuk dari PLC akan muncul di tabel "Order" dan "Delivered"
   - Waktu dan uptime sistem ditampilkan di bagian atas

5. **Melihat Laporan**
   - Klik tombol "Open Log Folder" untuk membuka folder berisi file CSV harian
   - File bisa dibuka dengan Excel untuk dianalisis atau dicetak

---

## **Fitur-Fitur Utama**

- **Koneksi Otomatis**: Program akan terus mencoba menghubungkan ke PLC dan memperbarui status secara real-time
- **Anti Data Duplikat**: Sistem pintar yang mengenali data baru vs data lama, sehingga tidak ada pencatatan ganda
- **Logging Industrial Grade**: Sistem retry otomatis jika file sedang dibuka aplikasi lain (seperti Excel)
- **Single Instance Protection**: Mencegah aplikasi dibuka lebih dari satu kali untuk menghindari konflik data
- **Pencatatan Error**: Semua masalah teknis tercatat di `error_log.txt` untuk troubleshooting
- **Format Waktu Indonesia**: Menggunakan format 24 jam yang familiar (HH:mm:ss)

---

## **Tips Penggunaan**

- Pastikan komputer dan PLC terhubung dalam jaringan yang sama
- Jangan membuka file CSV di Excel saat program sedang mencatat data (bisa menyebabkan konflik)
- Jika status selalu "OFFLINE", periksa kembali alamat IP dan pastikan PLC menyala
- Backup file `config.txt` jika ingin memindahkan pengaturan ke komputer lain

---

**Catatan**: Program ini menggunakan protokol komunikasi standar TCP/IP untuk PLC Keyence. Untuk PLC merek lain, mungkin memerlukan penyesuaian protokol komunikasi.
