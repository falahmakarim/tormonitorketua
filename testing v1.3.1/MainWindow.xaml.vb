Imports System.Collections.ObjectModel
Imports System.ComponentModel
Imports System.Net.Sockets
Imports System.Text
Imports System.Windows.Threading
Imports System.Linq
Imports System.IO
Imports System.Threading.Tasks
Imports System.Text.RegularExpressions

Class MainWindow
    ' --- VARIABEL KONFIGURASI (DYNAMIC) ---
    Private PLC_IP As String = "127.0.0.1"
    Private PLC_PORT As Integer = 8501
    Private MEMORY_ADDR As String = "DM600"
    Private CustomLogPath As String = ""

    Private Const WORD_COUNT As Integer = 80
    Private Const MAX_ROWS As Integer = 8
    Private ReadOnly ConfigFile As String = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.txt")
    Private ReadOnly ErrorLogFile As String = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error_log.txt")

    Private StartTime As DateTime
    Public Property ListDelivered As New ObservableCollection(Of SingleItem)
    Public Property ListOrder As New ObservableCollection(Of DualItem)

    Private ReadOnly DataLock As New Object() ' Lock untuk Data In-Memory
    Private ReadOnly FileLock As New Object() ' Lock untuk File Writing
    Private PendingDataQueue As New Queue(Of String)()
    Private WithEvents TimerPLC As DispatcherTimer
    Private WithEvents TimerProcessQueue As DispatcherTimer

    Private StaticClient As TcpClient
    Private IsReading As Boolean = False
    Private LastCountInPLC As Integer = 0
    Private LastRawData As String = ""

    ' --- INITIALIZATION ---
    Private Sub Window_Loaded(sender As Object, e As RoutedEventArgs)
        ' Mencegah Double Instance (Aplikasi dijalankan 2x)
        If Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName).Length > 1 Then
            MessageBox.Show("Aplikasi sudah berjalan.", "Peringatan", MessageBoxButton.OK, MessageBoxImage.Warning)
            Application.Current.Shutdown()
            Exit Sub
        End If

        StartTime = DateTime.Now
        LoadConfig()

        If String.IsNullOrEmpty(CustomLogPath) OrElse Not Directory.Exists(CustomLogPath) Then
            PilihFolderBaru()
        End If

        SetupTables()
        SetupTimer()
        SetupQueueProcessTimer()
        UpdateConnectionStatus(False)
        TulisErrorLog("System Started.") ' Log tanda sistem nyala
    End Sub

    ' --- LOGIKA KONEKSI (ROBUST VERSION) ---
    Private Async Function ReadFromPLC() As Task(Of String)
        Try
            Dim inputIP = TxtIPAddress.Text.Trim()
            Dim inputPort As Integer
            If Not Integer.TryParse(TxtPort.Text.Trim(), inputPort) Then inputPort = 8501
            Dim inputAddr = TxtMemAddr.Text.Trim()

            ' Deteksi perubahan Config
            If inputIP <> PLC_IP OrElse inputPort <> PLC_PORT Then
                PLC_IP = inputIP
                PLC_PORT = inputPort
                ' Bersihkan koneksi lama dengan aman
                If StaticClient IsNot Nothing Then
                    StaticClient.Close()
                    StaticClient.Dispose()
                    StaticClient = Nothing
                End If
            End If
            MEMORY_ADDR = inputAddr

            ' Re-connect Logic
            If StaticClient Is Nothing Then
                StaticClient = New TcpClient()
                StaticClient.NoDelay = True ' Matikan Nagle Algorithm untuk kecepatan
                StaticClient.ReceiveTimeout = 2000 ' Timeout Safety
                StaticClient.SendTimeout = 2000
            End If

            If Not StaticClient.Connected Then
                Dim connectTask = StaticClient.ConnectAsync(PLC_IP, PLC_PORT)
                ' Wait with Timeout 1.5s
                If Await Task.WhenAny(connectTask, Task.Delay(1500)) IsNot connectTask Then
                    ' Force close jika timeout
                    StaticClient.Close()
                    StaticClient = Nothing
                    Throw New Exception("Connection Timeout (PLC Not Reachable)")
                End If
            End If

            Dim stream = StaticClient.GetStream()
            Dim cmd = Encoding.ASCII.GetBytes($"RDS {MEMORY_ADDR} {WORD_COUNT}{vbCr}")
            Await stream.WriteAsync(cmd, 0, cmd.Length)

            ' Sedikit delay untuk memberi waktu PLC memproses
            Await Task.Delay(50)

            ' Buffer Reading
            If StaticClient.Available > 0 OrElse True Then ' Force read attempt
                Dim buffer(4096) As Byte
                ' ReadAsync dengan Cancellation Token (Implicit via Timeout properties di atas)
                Dim bytesRead = Await stream.ReadAsync(buffer, 0, buffer.Length)
                UpdateConnectionStatus(True)
                Return Encoding.ASCII.GetString(buffer, 0, bytesRead).Trim()
            End If

            Return ""

        Catch ex As Exception
            ' RESOURCE CLEANUP: Pastikan socket mati supaya tidak "hang"
            If StaticClient IsNot Nothing Then
                StaticClient.Close()
                StaticClient = Nothing
            End If

            UpdateConnectionStatus(False)

            ' Log Error tapi jangan spam (Opsional: bisa dikasih timer biar gak log tiap detik)
            ' TulisErrorLog($"Connection Error: {ex.Message}") 
            Return ""
        End Try
    End Function

    Private Async Sub TimerPLC_Tick(sender As Object, e As EventArgs) Handles TimerPLC.Tick
        LabelWaktu.Text = DateTime.Now.ToString("HH.mm.ss")
        UpdateUptime()

        If IsReading Then Exit Sub
        IsReading = True

        Try
            Dim responsePLC = Await ReadFromPLC()

            ' 1. Cek Validitas Respon
            ' Kalau respon kosong string "" artinya KONEKSI PUTUS/ERROR.
            ' Jangan lakukan apa-apa. Jangan reset ingatan. Biarkan LastRawData tetap data lama.
            If String.IsNullOrEmpty(responsePLC) Then
                Exit Sub
            End If

            ' Bersihkan respon (Hanya angka dan spasi)
            Dim cleanRes As String = Regex.Replace(responsePLC, "[^0-9\s]", "").Trim()
            TextBoxRawData.Text = $"[{DateTime.Now:HH.mm.ss}] {cleanRes}"

            ' 2. Analisis Isi Data
            Dim words = cleanRes.Split({" "c}, StringSplitOptions.RemoveEmptyEntries)

            ' Hitung total nilai (Checksum sederhana) untuk cek apakah isinya 0 semua
            Dim isAllZero As Boolean = True
            For Each w In words
                If w <> "0" AndAlso w <> "00" AndAlso w <> "0000" AndAlso w <> "00000" Then
                    isAllZero = False
                    Exit For
                End If
            Next

            SyncLock DataLock
                ' KONDISI A: PLC MENGIRIM DATA KOSONG / RESET (0 0 0...)
                If isAllZero Then
                    ' Ini adalah momen "Lepas Tombol".
                    ' Kita hapus ingatan LastRawData supaya nanti kalau ada data masuk lagi (walau sama),
                    ' kita anggap itu baru.
                    LastRawData = ""
                    LastCountInPLC = 0

                    ' KONDISI B: PLC MENGIRIM DATA BERISI (Ada Modelnya)
                Else
                    ' Cek apakah data ini SAMA PERSIS dengan yang barusan kita proses?
                    If cleanRes = LastRawData Then
                        ' STOP! INI DUPLIKAT!
                        ' PLC belum mereset data, atau ini data sisa.
                        ' Jangan dimasukkan ke antrean.
                        ' (Diam saja)
                    Else
                        ' INI DATA BARU! (Karena beda dengan ingatan terakhir)
                        ' Ingatan terakhir kita tadi kosong (karena sudah reset), sekarang ada isinya.
                        ' Maka ini dianggap Batch Baru.

                        Dim currentModels As New List(Of String)
                        If words.Length >= 5 Then
                            For i As Integer = 0 To words.Length - 5 Step 5
                                Dim chunk = words.Skip(i).Take(5).ToArray()
                                Dim modelName = ConvertChunkToASCII(chunk)
                                If Not String.IsNullOrEmpty(modelName) AndAlso modelName <> "KOSONG" Then
                                    currentModels.Add(modelName)
                                End If
                            Next
                        End If

                        ' Masukkan ke Antrean
                        For Each mBaru In currentModels
                            PendingDataQueue.Enqueue(mBaru)
                            SimpanKeLog(mBaru)
                        Next

                        ' Update Ingatan
                        LastRawData = cleanRes
                        LastCountInPLC = currentModels.Count
                    End If
                End If
            End SyncLock

        Catch ex As Exception
            ' Jangan update status disini biar gak kedip-kedip
        Finally
            IsReading = False
        End Try
    End Sub

    ' --- LOGIKA PENGISIAN TABLE ---
    Private Sub TimerProcessQueue_Tick(sender As Object, e As EventArgs) Handles TimerProcessQueue.Tick
        SyncLock DataLock
            While PendingDataQueue.Count > 0
                Dim targetA = ListOrder.FirstOrDefault(Function(x) String.IsNullOrEmpty(x.Value1))
                If targetA IsNot Nothing Then
                    targetA.Value1 = PendingDataQueue.Dequeue()
                Else
                    Dim targetB = ListOrder.FirstOrDefault(Function(x) String.IsNullOrEmpty(x.Value2))
                    If targetB IsNot Nothing Then
                        targetB.Value2 = PendingDataQueue.Dequeue()
                    Else
                        Exit While
                    End If
                End If
            End While
        End SyncLock
    End Sub

    ' --- SHIFT REGISTER ---
    Private Sub ProsesShiftRegister()
        SyncLock DataLock
            For i As Integer = 0 To MAX_ROWS - 1
                ListDelivered(i).Value = ListOrder(i).Value1
                ListOrder(i).Value1 = ListOrder(i).Value2
                If PendingDataQueue.Count > 0 Then
                    ListOrder(i).Value2 = PendingDataQueue.Dequeue()
                Else
                    ListOrder(i).Value2 = ""
                End If
            Next
        End SyncLock
    End Sub

    ' --- FITUR BARU: INDUSTRIAL GRADE LOGGING (RETRY PATTERN) ---
    Private Sub SimpanKeLog(modelName As String)
        ' Fitur Retry: Mencoba menulis 3 kali jika file terkunci (misal dibuka Excel)
        Dim MaxRetries As Integer = 3
        Dim DelayMs As Integer = 100 ' Jeda 0.1 detik antar percobaan

        Task.Run(Sub()
                     For attempt As Integer = 1 To MaxRetries
                         Try
                             SyncLock FileLock
                                 If Not Directory.Exists(CustomLogPath) Then Directory.CreateDirectory(CustomLogPath)
                                 Dim filePath = Path.Combine(CustomLogPath, $"Produksi_{DateTime.Now:dd-MM-yyyy}.csv")

                                 ' Cek apakah perlu header
                                 Dim needHeader As Boolean = Not File.Exists(filePath) OrElse New FileInfo(filePath).Length = 0

                                 Using fs As New FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)
                                     Using sw As New StreamWriter(fs)
                                         If needHeader Then sw.WriteLine("Waktu,Model Kulkas")
                                         sw.WriteLine($"{DateTime.Now:HH:mm:ss},{modelName}")
                                     End Using
                                 End Using
                             End SyncLock
                             Exit Sub ' Berhasil, keluar dari loop
                         Catch ex As IOException
                             ' Kemungkinan file dilock Excel
                             If attempt < MaxRetries Then
                                 System.Threading.Thread.Sleep(DelayMs) ' Tunggu sebentar
                             Else
                                 TulisErrorLog($"Gagal simpan CSV (File Locked): {modelName}. Error: {ex.Message}")
                             End If
                         Catch ex As Exception
                             TulisErrorLog($"Error Simpan CSV: {ex.Message}")
                             Exit Sub
                         End Try
                     Next
                 End Sub)
    End Sub

    ' --- FITUR BARU: SYSTEM ERROR LOGGING ---
    Private Sub TulisErrorLog(msg As String)
        Try
            SyncLock FileLock
                Dim logMsg As String = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}"
                File.AppendAllText(ErrorLogFile, logMsg)
            End SyncLock
        Catch
            ' Silent fail jika disk full atau error parah
        End Try
    End Sub

    ' --- HELPER FUNCTIONS ---
    Private Sub LoadConfig()
        Try
            If File.Exists(ConfigFile) Then
                Dim content = File.ReadAllText(ConfigFile).Split("|"c)
                If content.Length >= 4 Then
                    PLC_IP = content(0)
                    PLC_PORT = If(Integer.TryParse(content(1), Nothing), CInt(content(1)), 8501)
                    MEMORY_ADDR = content(2)
                    CustomLogPath = content(3)
                    TxtIPAddress.Text = PLC_IP
                    TxtPort.Text = PLC_PORT.ToString()
                    TxtMemAddr.Text = MEMORY_ADDR
                End If
            End If
        Catch : End Try
    End Sub

    Private Sub SaveConfig()
        Try
            Dim dataToSave = $"{TxtIPAddress.Text.Trim()}|{TxtPort.Text.Trim()}|{TxtMemAddr.Text.Trim()}|{CustomLogPath}"
            File.WriteAllText(ConfigFile, dataToSave)
        Catch : End Try
    End Sub

    Private Sub Input_LostFocus(sender As Object, e As RoutedEventArgs)
        SaveConfig()
    End Sub

    Private Sub PilihFolderBaru()
        Try
            Dim dialog As New Microsoft.Win32.OpenFolderDialog()
            If dialog.ShowDialog() = True Then
                CustomLogPath = dialog.FolderName
                SaveConfig()
            End If
        Catch : End Try
    End Sub

    Private Function ConvertChunkToASCII(chunk As String()) As String
        Dim sb As New StringBuilder()
        For Each w In chunk
            Dim val As Integer
            If Integer.TryParse(w, val) AndAlso val > 0 Then
                Dim h = CByte((val >> 8) And &HFF), l = CByte(val And &HFF)
                If h >= 32 AndAlso h <= 126 Then sb.Append(Chr(h))
                If l >= 32 AndAlso l <= 126 Then sb.Append(Chr(l))
            End If
        Next
        Return sb.ToString().Trim()
    End Function

    Private Sub UpdateConnectionStatus(isConnected As Boolean)
        If Me.Dispatcher.HasShutdownStarted Then Exit Sub
        Me.Dispatcher.BeginInvoke(Sub()
                                      If isConnected Then
                                          InfoConnStatus.Text = "ONLINE"
                                          InfoConnStatus.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green)
                                          LabelStatus.Text = "PLC CONNECTED"
                                          BottomStatusBar.Background = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.ForestGreen)
                                      Else
                                          InfoConnStatus.Text = "OFFLINE"
                                          InfoConnStatus.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red)
                                          LabelStatus.Text = "WAITING FOR CONNECTION..."
                                          BottomStatusBar.Background = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Crimson)
                                      End If
                                  End Sub)
    End Sub

    Private Sub SetupTables()
        For i As Integer = 1 To MAX_ROWS
            ListDelivered.Add(New SingleItem())
            ListOrder.Add(New DualItem())
        Next
        GridDelivered.ItemsSource = ListDelivered
        GridOrder.ItemsSource = ListOrder
    End Sub

    Private Sub SetupTimer()
        TimerPLC = New DispatcherTimer()
        TimerPLC.Interval = TimeSpan.FromMilliseconds(250)
        TimerPLC.Start()
    End Sub

    Private Sub SetupQueueProcessTimer()
        TimerProcessQueue = New DispatcherTimer()
        TimerProcessQueue.Interval = TimeSpan.FromMilliseconds(200)
        TimerProcessQueue.Start()
    End Sub

    Private Sub UpdateUptime()
        Dim diff As TimeSpan = DateTime.Now - StartTime
        InfoUptime.Text = String.Format("{0:00}:{1:00}:{2:00}", Math.Floor(diff.TotalHours), diff.Minutes, diff.Seconds)
    End Sub

    Private Sub Expander_Expanded(sender As Object, e As RoutedEventArgs)
        Me.Focus()
    End Sub

    Private Sub Expander_Collapsed(sender As Object, e As RoutedEventArgs)
        Me.Focus()
    End Sub

    Private Sub Window_KeyDown(sender As Object, e As KeyEventArgs)
        If e.Key = Key.Enter OrElse e.Key = Key.Space Then
            ProsesShiftRegister()
            e.Handled = True
            Me.Focus()
        End If
    End Sub

    Private Sub BtnDeliver_Click(sender As Object, e As RoutedEventArgs)
        ProsesShiftRegister()
    End Sub

    Private Sub BtnSetPath_Click(sender As Object, e As RoutedEventArgs)
        PilihFolderBaru()
    End Sub

    Private Sub BtnOpenLog_Click(sender As Object, e As RoutedEventArgs)
        Try
            If Directory.Exists(CustomLogPath) Then Process.Start("explorer.exe", CustomLogPath)
        Catch : End Try
    End Sub
End Class

' --- DATA MODELS ---
Public Class SingleItem
    Implements INotifyPropertyChanged
    Private _val As String = ""
    Public Property Value As String
        Get
            Return _val
        End Get
        Set(value As String)
            _val = value : RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs("Value"))
        End Set
    End Property
    Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged
End Class

Public Class DualItem
    Implements INotifyPropertyChanged
    Private _val1 As String = ""
    Private _val2 As String = ""

    Public Property Value1 As String
        Get
            Return _val1
        End Get
        Set(value As String)
            _val1 = value
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs("Value1"))
        End Set
    End Property

    Public Property Value2 As String
        Get
            Return _val2
        End Get
        Set(value As String)
            _val2 = value
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs("Value2"))
        End Set
    End Property

    Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged
End Class