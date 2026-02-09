Imports System.Collections.ObjectModel
Imports System.ComponentModel
Imports System.Net.Sockets
Imports System.Text
Imports System.Windows.Threading
Imports System.Linq ' WAJIB: Agar .All dan .FirstOrDefault tidak error

Class MainWindow
    ' --- KONFIGURASI PLC ---
    Private Const PLC_IP As String = "192.168.0.10"
    Private Const PLC_PORT As Integer = 8501
    Private Const MEMORY_ADDR As String = "DM600"
    Private Const WORD_COUNT As Integer = 80 ' Mendukung 16 model sekaligus
    Private Const MAX_ROWS As Integer = 8

    ' --- PROPERTI & VARIABEL ---
    Public Property ListDelivered As ObservableCollection(Of SingleItem)
    Public Property ListOrder As ObservableCollection(Of DualItem)

    Private ReadOnly DataLock As New Object()
    Private PendingDataQueue As New Queue(Of String)()
    Private WithEvents TimerPLC As DispatcherTimer
    Private WithEvents TimerProcessQueue As DispatcherTimer
    Private StaticClient As TcpClient

    ' Flag & Tracker Stabilitas
    Private IsReading As Boolean = False
    Private LastCountInPLC As Integer = 0

    ' --- INISIALISASI ---
    Private Sub Window_Loaded(sender As Object, e As RoutedEventArgs)
        SetupTables()
        SetupTimer()
        SetupQueueProcessTimer()
        UpdateInfoPanel()
        UpdateConnectionStatus(False)
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
        TimerPLC.Interval = TimeSpan.FromMilliseconds(150) ' Diperlonggar biar socket gak stres
        TimerPLC.Start()
    End Sub

    Private Sub SetupQueueProcessTimer()
        TimerProcessQueue = New DispatcherTimer()
        TimerProcessQueue.Interval = TimeSpan.FromMilliseconds(200)
        TimerProcessQueue.Start()
    End Sub

    Private Sub UpdateInfoPanel()
        InfoIPAddress.Text = PLC_IP
        InfoPort.Text = PLC_PORT.ToString()
        InfoMemAddr.Text = MEMORY_ADDR
    End Sub

    ' --- LOGIKA UTAMA: INDEX-BASED & ANTI-FLICKER ---

    Private Sub TimerPLC_Tick(sender As Object, e As EventArgs) Handles TimerPLC.Tick
        If IsReading Then Exit Sub ' Cegah timer tabrakan

        Try
            IsReading = True
            LabelWaktu.Text = DateTime.Now.ToString("HH.mm.ss")

            Dim responsePLC As String = ReadFromPLC()
            If String.IsNullOrEmpty(responsePLC) OrElse responsePLC = "KOSONG" Then Exit Sub

            Dim cleanResponse = System.Text.RegularExpressions.Regex.Replace(responsePLC, "[^0-9\s]", "").Trim()
            TextBoxRawData.Text = $"[{DateTime.Now:HH.mm.ss}] {cleanResponse}"

            Dim words = cleanResponse.Split({" "c}, StringSplitOptions.RemoveEmptyEntries)
            Dim currentModelsInPLC As New List(Of String)

            ' 1. Parsing semua model yang ada di PLC sekarang
            If words.Length >= 5 Then
                For i As Integer = 0 To words.Length - 5 Step 5
                    Dim chunk = words.Skip(i).Take(5).ToArray()
                    Dim modelName = ConvertChunkToASCII(chunk)
                    If modelName <> "KOSONG" AndAlso modelName <> "" Then
                        currentModelsInPLC.Add(modelName)
                    End If
                Next
            End If

            SyncLock DataLock
                ' 2. Reset otomatis kalau tabel kosong (habis tekan Enter)
                Dim isTableEmpty = ListOrder.All(Function(x) String.IsNullOrEmpty(x.Value1))
                If isTableEmpty AndAlso PendingDataQueue.Count = 0 Then
                    LastCountInPLC = 0
                End If

                ' 3. Ambil data berdasarkan urutan (Index), bukan cuma nama model
                If currentModelsInPLC.Count > LastCountInPLC Then
                    For i As Integer = LastCountInPLC To currentModelsInPLC.Count - 1
                        PendingDataQueue.Enqueue(currentModelsInPLC(i))
                    Next
                    LastCountInPLC = currentModelsInPLC.Count
                ElseIf currentModelsInPLC.Count < LastCountInPLC Then
                    LastCountInPLC = currentModelsInPLC.Count
                End If
            End SyncLock

        Catch ex As Exception
            ' Silakan tambah log error di sini kalau mau
        Finally
            IsReading = False
        End Try
    End Sub

    ' --- PEMROSESAN TAMPILAN ---

    Private Sub TimerProcessQueue_Tick(sender As Object, e As EventArgs) Handles TimerProcessQueue.Tick
        SyncLock DataLock
            If PendingDataQueue.Count > 0 Then
                Dim dataBaru = PendingDataQueue.Dequeue()
                TampilkanDataKeTableOrder(dataBaru)
            End If
        End SyncLock
    End Sub

    Private Sub TampilkanDataKeTableOrder(dataBaru As String)
        Dim targetItem = ListOrder.FirstOrDefault(Function(x) String.IsNullOrEmpty(x.Value1))
        If targetItem IsNot Nothing Then
            targetItem.Value1 = dataBaru
        Else
            Dim bufferItem = ListOrder.FirstOrDefault(Function(x) String.IsNullOrEmpty(x.Value2))
            If bufferItem IsNot Nothing Then bufferItem.Value2 = dataBaru
        End If
    End Sub

    ' --- TOMBOL ENTER (SHIFT REGISTER) ---

    Private Sub Window_KeyDown(sender As Object, e As KeyEventArgs)
        If e.Key = Key.Enter Then
            SyncLock DataLock
                For i As Integer = 0 To MAX_ROWS - 1
                    ListDelivered(i).Value = ListOrder(i).Value1
                    ListOrder(i).Value1 = ListOrder(i).Value2
                    ListOrder(i).Value2 = ""
                Next
            End SyncLock
            e.Handled = True
        End If
    End Sub

    ' --- KOMUNIKASI TCP ---

    Private Function ReadFromPLC() As String
        Try
            EnsureConnection()
            Dim stream As NetworkStream = StaticClient.GetStream()
            While stream.DataAvailable : stream.ReadByte() : End While

            Dim cmd As String = $"RDS {MEMORY_ADDR} {WORD_COUNT}{vbCr}"
            Dim bytesToSend = Encoding.ASCII.GetBytes(cmd)
            stream.Write(bytesToSend, 0, bytesToSend.Length)

            If WaitForData(stream, 500) Then ' Timeout lebih longgar buat data besar
                Dim buffer(8192) As Byte
                Dim bytesRead = stream.Read(buffer, 0, buffer.Length)
                UpdateConnectionStatus(True)
                Return Encoding.ASCII.GetString(buffer, 0, bytesRead).Trim()
            End If
            Return "KOSONG"
        Catch
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
        End If
    End Sub

    Private Sub ResetConnection()
        If StaticClient IsNot Nothing Then StaticClient.Close()
        StaticClient = Nothing
    End Sub

    Private Function WaitForData(stream As NetworkStream, timeoutMs As Integer) As Boolean
        Dim elapsed = 0
        While Not stream.DataAvailable AndAlso elapsed < timeoutMs
            System.Threading.Thread.Sleep(10) : elapsed += 10
        End While
        Return stream.DataAvailable
    End Function

    Private Function ConvertChunkToASCII(chunk As String()) As String
        Dim sb As New StringBuilder()
        For Each w In chunk
            Dim val As Integer
            If Integer.TryParse(w, val) AndAlso val > 0 Then
                Dim high = CByte((val >> 8) And &HFF)
                Dim low = CByte(val And &HFF)
                If high >= 32 AndAlso high <= 126 Then sb.Append(Chr(high))
                If low >= 32 AndAlso low <= 126 Then sb.Append(Chr(low))
            End If
        Next
        Return sb.ToString().Trim()
    End Function

    Private Sub UpdateConnectionStatus(isConnected As Boolean)
        InfoConnStatus.Text = If(isConnected, "ONLINE", "OFFLINE")
        InfoConnStatus.Foreground = New System.Windows.Media.SolidColorBrush(If(isConnected, System.Windows.Media.Colors.Green, System.Windows.Media.Colors.Red))
        LabelStatus.Text = If(isConnected, "PLC CONNECTED", "WAITING FOR CONNECTION...")
    End Sub
End Class

' --- MODELS (WAJIB ADA DI BAWAH) ---
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
    Private _v1 As String = "" : Private _v2 As String = ""
    Public Property Value1 As String
        Get
            Return _v1
        End Get
        Set(value As String)
            _v1 = value : RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs("Value1"))
        End Set
    End Property
    Public Property Value2 As String
        Get
            Return _v2
        End Get
        Set(value As String)
            _v2 = value : RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs("Value2"))
        End Set
    End Property
    Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged
End Class