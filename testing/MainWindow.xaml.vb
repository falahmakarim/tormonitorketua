Imports System.Collections.ObjectModel
Imports System.ComponentModel
Imports System.Net.Sockets
Imports System.Text
Imports System.Windows.Threading

Class MainWindow

    ' =======================================================================
    '                         KONFIGURASI UTAMA
    ' =======================================================================

    ' PILIH MODE (UNCOMMENT SALAH SATU)
    Private Const IS_SIMULATION As Boolean = False ' <--- MODE REAL (KONEK PLC)
    'Private Const IS_SIMULATION As Boolean = True' <--- MODE SIMULASI (TESTING)

    ' SETTING PLC
    Private Const PLC_IP As String = "192.168.0.10"
    Private Const PLC_PORT As Integer = 8500
    Private Const MEMORY_ADDR As String = "DM1000"
    Private Const WORD_COUNT As Integer = 10

    ' SETTING TABEL
    Private Const MAX_ROWS As Integer = 8

    ' =======================================================================

    Public ListDelivered As ObservableCollection(Of SingleItem)
    Public ListOrder As ObservableCollection(Of DualItem)

    Private TimerPLC As DispatcherTimer
    Private RandomGen As New Random()
    Private SimTicker As Integer = 0

    ' --- STARTUP ---
    Private Sub Window_Loaded(sender As Object, e As RoutedEventArgs)
        ListDelivered = New ObservableCollection(Of SingleItem)()
        ListOrder = New ObservableCollection(Of DualItem)()

        For i As Integer = 1 To MAX_ROWS
            ListDelivered.Add(New SingleItem With {.Value = ""})
            ListOrder.Add(New DualItem With {.Value1 = "", .Value2 = ""})
        Next

        GridDelivered.ItemsSource = ListDelivered
        GridOrder.ItemsSource = ListOrder

        TimerPLC = New DispatcherTimer()
        TimerPLC.Interval = TimeSpan.FromSeconds(1)
        AddHandler TimerPLC.Tick, AddressOf TimerPLC_Tick
        TimerPLC.Start()

        LabelWaktu.Text = DateTime.Now.ToString("dd MMM yyyy")

        If IS_SIMULATION Then
            UpdateStatus(True, "MODE SIMULASI AKTIF")
        Else
            UpdateStatus(False, "Menunggu Koneksi PLC...")
        End If
    End Sub

    ' --- TIMER LOOP (LOGIKA PENGISIAN URUT) ---
    Private Sub TimerPLC_Tick(sender As Object, e As EventArgs)
        LabelWaktu.Text = DateTime.Now.ToString("HH:mm:ss")

        Dim rawData As String = ""

        If IS_SIMULATION Then
            UpdateStatus(True, "PLC TERHUBUNG (SIMULASI)")
            rawData = GenerateDummyData()
        Else
            rawData = ReadFromPLC()
        End If

        ' Jika ada data baru masuk
        If Not String.IsNullOrEmpty(rawData) Then

            ' Cek Duplikat
            Dim isDuplicate As Boolean = False
            For Each item In ListOrder
                If item.Value1 = rawData Or item.Value2 = rawData Then isDuplicate = True
            Next

            If Not isDuplicate Then
                Dim dataSaved As Boolean = False

                ' 1. Isi Kolom ORDER A (Priority)
                For i As Integer = 0 To MAX_ROWS - 1
                    If String.IsNullOrEmpty(ListOrder(i).Value1) Then
                        ListOrder(i).Value1 = rawData
                        dataSaved = True
                        Exit For
                    End If
                Next

                ' 2. Jika A Penuh, Isi Kolom ORDER B (Buffer)
                If Not dataSaved Then
                    For i As Integer = 0 To MAX_ROWS - 1
                        If String.IsNullOrEmpty(ListOrder(i).Value2) Then
                            ListOrder(i).Value2 = rawData
                            Exit For
                        End If
                    Next
                End If

            End If
        End If
    End Sub

    ' ------------------------------------------------------------------
    '   LOGIKA TOMBOL ENTER (TRANSFER TOTAL & REPLACE)
    ' ------------------------------------------------------------------
    Private Sub Window_KeyDown(sender As Object, e As KeyEventArgs)
        If e.Key = Key.Enter Then

            ' Kita loop SEMUA SLOT (1-8)
            For i As Integer = 0 To MAX_ROWS - 1
                Dim itemOrder = ListOrder(i)
                Dim itemDelivered = ListDelivered(i)

                ' 1. REPLACE DATA DELIVERED DENGAN ORDER A
                ' Kita TIDAK melakukan cek If Not Empty.
                ' Langsung timpa saja!
                ' Jika Order A ada isinya -> Delivered terisi.
                ' Jika Order A KOSONG -> Delivered ikut KOSONG (Data lama terhapus).
                itemDelivered.Value = itemOrder.Value1

                ' 2. GESER ORDER B KE A
                ' Sama, langsung timpa. Kalau B kosong, A jadi kosong.
                itemOrder.Value1 = itemOrder.Value2

                ' 3. BERSIHKAN ORDER B
                itemOrder.Value2 = ""
            Next

            ' (Optional) Sound Beep
            ' System.Media.SystemSounds.Beep.Play()
        End If
    End Sub

    ' --- SIMULASI ---
    Private Function GenerateDummyData() As String
        SimTicker += 1
        If SimTicker >= 1 Then
            SimTicker = 0
            Dim types As String() = {"MODEL-X", "MODEL-Y", "MODEL-Z", "PRO-MAX"}
            Dim idx As Integer = RandomGen.Next(types.Length)
            Return types(idx) & "-" & RandomGen.Next(100, 999).ToString()
        End If
        Return ""
    End Function

    ' --- KONEKSI PLC ---
    Private Function ReadFromPLC() As String
        Dim client As TcpClient = Nothing
        Try
            client = New TcpClient()
            Dim result = client.BeginConnect(PLC_IP, PLC_PORT, Nothing, Nothing)
            Dim success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(500))

            If Not success Then
                UpdateStatus(False, "PLC NOT RESPONDING")
                Return ""
            End If

            client.EndConnect(result)
            UpdateStatus(True, "PLC CONNECTED")

            Dim stream As NetworkStream = client.GetStream()
            stream.ReadTimeout = 2000

            Dim cmd As String = $"RDS {MEMORY_ADDR} {WORD_COUNT}" & vbCr
            Dim dataToSend As Byte() = Encoding.ASCII.GetBytes(cmd)
            stream.Write(dataToSend, 0, dataToSend.Length)

            Dim buffer(1024) As Byte
            Dim bytesRead As Integer = stream.Read(buffer, 0, buffer.Length)
            Dim response As String = Encoding.ASCII.GetString(buffer, 0, bytesRead).Trim()

            If Not response.StartsWith("E") AndAlso response.Length > 0 Then
                Return ConvertKeyenceResponseToText(response)
            End If
        Catch ex As Exception
            UpdateStatus(False, "PLC NOT RESPONDING")
        Finally
            If client IsNot Nothing Then client.Close()
        End Try
        Return ""
    End Function

    Private Function ConvertKeyenceResponseToText(rawResponse As String) As String
        Dim sb As New StringBuilder()
        Dim words As String() = rawResponse.Split(New Char() {" "c}, StringSplitOptions.RemoveEmptyEntries)
        For Each wordStr As String In words
            Dim wordInt As Integer
            If Integer.TryParse(wordStr, wordInt) Then
                Dim lowByte As Byte = CByte(wordInt And &HFF)
                Dim highByte As Byte = CByte((wordInt >> 8) And &HFF)
                If lowByte > 0 Then sb.Append(Chr(lowByte))
                If highByte > 0 Then sb.Append(Chr(highByte))
            End If
        Next
        Return sb.ToString().Trim()
    End Function

    Private Sub UpdateStatus(isConnected As Boolean, msg As String)
        LabelStatus.Text = msg
        If isConnected Then
            LabelStatus.Foreground = Brushes.LimeGreen
        Else
            LabelStatus.Foreground = Brushes.Red
        End If
    End Sub

End Class

' --- MODEL DATA ---
Public Class SingleItem
    Implements INotifyPropertyChanged
    Private _val As String
    Public Property Value As String
        Get
            Return _val
        End Get
        Set(value As String)
            _val = value
            OnPropertyChanged("Value")
        End Set
    End Property
    Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged
    Protected Sub OnPropertyChanged(name As String)
        RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(name))
    End Sub
End Class

Public Class DualItem
    Implements INotifyPropertyChanged
    Private _val1 As String
    Private _val2 As String
    Public Property Value1 As String
        Get
            Return _val1
        End Get
        Set(value As String)
            _val1 = value
            OnPropertyChanged("Value1")
        End Set
    End Property
    Public Property Value2 As String
        Get
            Return _val2
        End Get
        Set(value As String)
            _val2 = value
            OnPropertyChanged("Value2")
        End Set
    End Property
    Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged
    Protected Sub OnPropertyChanged(name As String)
        RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(name))
    End Sub
End Class