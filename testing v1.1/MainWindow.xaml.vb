Imports System.Collections.ObjectModel
Imports System.ComponentModel
Imports System.Net.Sockets
Imports System.Text
Imports System.Windows.Threading

Class MainWindow
    ' --- KONFIGURASI ---
    Private Const PLC_IP As String = "192.168.0.10"
    Private Const PLC_PORT As Integer = 8501
    Private Const MEMORY_ADDR As String = "DM600"
    Private Const WORD_COUNT As Integer = 10
    Private Const MAX_ROWS As Integer = 8

    ' Tambahan: Kapasitas Memory Buffer (Sesuai permintaan: 8 slot)
    Private Const MAX_MEMORY_SLOTS As Integer = 8

    ' --- PROPERTI & VARIABEL ---
    Public Property ListDelivered As ObservableCollection(Of SingleItem)
    Public Property ListOrder As ObservableCollection(Of DualItem)

    ' Antrean memori untuk menampung data sebelum masuk ke tabel UI
    Private MemoryQueue As New Queue(Of String)()

    Private WithEvents TimerPLC As DispatcherTimer
    Private WithEvents TimerUptime As DispatcherTimer
    Private StaticClient As TcpClient
    Private ConnectionStartTime As DateTime = Nothing
    Private LastProcessedData As String = ""
    Private LastDataTime As DateTime = DateTime.MinValue

    ' --- INISIALISASI ---
    Private Sub Window_Loaded(sender As Object, e As RoutedEventArgs)
        SetupTables()
        SetupTimer()
        SetupUptimeTimer()
        UpdateInfoPanel()
        UpdateStatus(False, "Menunggu Koneksi PLC...")
    End Sub

    Private Sub SetupTables()
        ListDelivered = New ObservableCollection(Of SingleItem)()
        ListOrder = New ObservableCollection(Of DualItem)()

        For i As Integer = 1 To MAX_ROWS
            ListDelivered.Add(New SingleItem())
            ListOrder.Add(New DualItem())
        Next

        GridDelivered.ItemsSource = ListDelivered
        GridOrder.ItemsSource = ListOrder
    End Sub

    Private Sub SetupTimer()
        TimerPLC = New DispatcherTimer()
        TimerPLC.Interval = TimeSpan.FromMilliseconds(100)
        TimerPLC.Start()
    End Sub

    Private Sub SetupUptimeTimer()
        TimerUptime = New DispatcherTimer()
        TimerUptime.Interval = TimeSpan.FromSeconds(1)
        TimerUptime.Start()
    End Sub

    Private Sub UpdateInfoPanel()
        InfoIPAddress.Text = PLC_IP
        InfoPort.Text = PLC_PORT.ToString()
        InfoMemAddr.Text = MEMORY_ADDR
    End Sub

    ' --- LOGIKA UTAMA ---
    Private Sub TimerPLC_Tick(sender As Object, e As EventArgs) Handles TimerPLC.Tick
        LabelWaktu.Text = DateTime.Now.ToString("HH.mm.ss")
        Dim rawData As String = ReadFromPLC()

        If IsDataValid(rawData) Then
            Dim timeSinceLastData = DateTime.Now - LastDataTime

            ' Jika data baru ATAU sudah 2 detik berlalu (mencegah duplikasi cepat)
            If rawData <> LastProcessedData OrElse timeSinceLastData.TotalSeconds > 2 Then
                SimpanDataKeSistem(rawData)
                LastProcessedData = rawData
                LastDataTime = DateTime.Now
                TextBoxRawData.Text = $"[{DateTime.Now:HH:mm:ss.fff}] {rawData}"
            End If
        End If
    End Sub

    ' Logika Penyimpanan: Cek Tabel -> Jika Penuh -> Masuk Memory
    Private Sub SimpanDataKeSistem(dataBaru As String)
        ' 1. Cari slot pertama yang Value1-nya benar-benar kosong di seluruh baris
        Dim targetItem = ListOrder.FirstOrDefault(Function(x) String.IsNullOrEmpty(x.Value1))

        If targetItem IsNot Nothing Then
            targetItem.Value1 = dataBaru
        Else
            ' 2. Jika seluruh Value1 penuh, cari slot pertama di Value2 (Buffer)
            Dim bufferItem = ListOrder.FirstOrDefault(Function(x) String.IsNullOrEmpty(x.Value2))
            If bufferItem IsNot Nothing Then
                bufferItem.Value2 = dataBaru
            Else
                ' 3. Jika tabel UI (A & B) benar-benar penuh, masuk ke Antrean Memori
                If MemoryQueue.Count < MAX_MEMORY_SLOTS Then
                    MemoryQueue.Enqueue(dataBaru)
                End If
            End If
        End If
    End Sub

    ' Tombol Enter: Pindah ke Delivered & Tarik data dari Memory ke Tabel
    Private Sub Window_KeyDown(sender As Object, e As KeyEventArgs)
        If e.Key = Key.Enter Then
            ' Loop untuk setiap baris (Shift Register Logic)
            For i As Integer = 0 To MAX_ROWS - 1
                ' 1. Selalu pindahkan apa yang ada di Value1 ke Delivered (Shift ke Kiri)
                ' Meskipun Value1 kosong, Delivered harus diupdate untuk sinkronisasi
                ListDelivered(i).Value = ListOrder(i).Value1

                ' 2. Geser Buffer (Value2) ke posisi Utama (Value1)
                ListOrder(i).Value1 = ListOrder(i).Value2

                ' 3. Tarik data dari MemoryQueue (Antrean Tersembunyi) ke posisi Buffer (Value2)
                If MemoryQueue.Count > 0 Then
                    ListOrder(i).Value2 = MemoryQueue.Dequeue()
                Else
                    ListOrder(i).Value2 = ""
                End If
            Next

            ' Reset deteksi agar jika barang yang sama lewat di PLC, sistem bisa menangkapnya lagi
            LastProcessedData = ""
        End If
    End Sub

    ' --- KOMUNIKASI PLC (KEYENCE RDS) ---
    Private Function ReadFromPLC() As String
        Try
            EnsureConnection()
            Dim stream As NetworkStream = StaticClient.GetStream()
            Dim cmd As String = $"RDS {MEMORY_ADDR} {WORD_COUNT}{vbCr}"
            Dim bytesToSend = Encoding.ASCII.GetBytes(cmd)
            stream.Write(bytesToSend, 0, bytesToSend.Length)

            If WaitForData(stream, 100) Then
                Dim buffer(1024) As Byte
                Dim bytesRead = stream.Read(buffer, 0, buffer.Length)
                Dim response = Encoding.ASCII.GetString(buffer, 0, bytesRead).Trim()
                UpdateConnectionStatus(True)
                Return ParseKeyenceResponse(response)
            End If
            Return "KOSONG"
        Catch ex As Exception
            ResetConnection()
            UpdateConnectionStatus(False)
            Return ""
        End Try
    End Function

    Private Sub EnsureConnection()
        If StaticClient Is Nothing OrElse Not StaticClient.Connected Then
            ResetConnection()
            StaticClient = New TcpClient()
            Dim result = StaticClient.BeginConnect(PLC_IP, PLC_PORT, Nothing, Nothing)
            If Not result.AsyncWaitHandle.WaitOne(1000) Then Throw New Exception("Timeout")
            StaticClient.EndConnect(result)
            ConnectionStartTime = DateTime.Now
        End If
    End Sub

    Private Sub ResetConnection()
        If StaticClient IsNot Nothing Then StaticClient.Close()
        StaticClient = Nothing
        ConnectionStartTime = Nothing
    End Sub

    Private Function WaitForData(stream As NetworkStream, timeoutMs As Integer) As Boolean
        Dim elapsed = 0
        While Not stream.DataAvailable AndAlso elapsed < timeoutMs
            System.Threading.Thread.Sleep(10)
            elapsed += 10
        End While
        Return stream.DataAvailable
    End Function

    Private Function ParseKeyenceResponse(rawResponse As String) As String
        Dim sb As New StringBuilder()
        Dim cleanData = System.Text.RegularExpressions.Regex.Replace(rawResponse, "[^0-9\s]", "")
        Dim words = cleanData.Split({" "c}, StringSplitOptions.RemoveEmptyEntries)
        For Each w In words
            Dim val As Integer
            If Integer.TryParse(w, val) Then
                ' Cuplikan perbaikan di ParseKeyenceResponse
                Dim high = CByte((val >> 8) And &HFF)
                Dim low = CByte(val And &HFF)
                ' Urutan: Low dulu baru High
                If low >= 32 AndAlso low <= 126 Then sb.Append(Chr(low))
                If high >= 32 AndAlso high <= 126 Then sb.Append(Chr(high))
            End If
        Next
        Dim result = sb.ToString().Trim()
        Return If(String.IsNullOrEmpty(result), "KOSONG", If(result.Length > 6, result.Substring(0, 6), result))
    End Function

    Private Sub UpdateStatus(isConnected As Boolean, msg As String)
        LabelStatus.Text = msg
    End Sub

    Private Sub UpdateConnectionStatus(isConnected As Boolean)
        If isConnected Then
            InfoConnStatus.Text = "ONLINE"
            InfoConnStatus.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green)
            LabelStatus.Text = "PLC CONNECTED"
        Else
            InfoConnStatus.Text = "OFFLINE"
            InfoConnStatus.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red)
            LabelStatus.Text = "OFFLINE"
        End If
    End Sub

    Private Function IsDataValid(data As String) As Boolean
        Return Not (String.IsNullOrEmpty(data) OrElse data = "KOSONG" OrElse data.Contains("E1"))
    End Function

    Private Sub TimerUptime_Tick(sender As Object, e As EventArgs) Handles TimerUptime.Tick
        If StaticClient IsNot Nothing AndAlso StaticClient.Connected Then
            Dim elapsed = DateTime.Now - ConnectionStartTime
            InfoUptime.Text = $"{elapsed.Hours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
        End If
    End Sub
End Class

' --- MODELS ---
Public Class SingleItem
    Implements INotifyPropertyChanged
    Private _val As String = ""
    Public Property Value As String
        Get
            Return _val
        End Get
        Set(value As String)
            _val = value
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs("Value"))
        End Set
    End Property
    Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged
End Class

Public Class DualItem
    Implements INotifyPropertyChanged
    Private _v1 As String = ""
    Private _v2 As String = ""
    Public Property Value1 As String
        Get
            Return _v1
        End Get
        Set(value As String)
            _v1 = value
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs("Value1"))
        End Set
    End Property
    Public Property Value2 As String
        Get
            Return _v2
        End Get
        Set(value As String)
            _v2 = value
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs("Value2"))
        End Set
    End Property
    Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged
End Class