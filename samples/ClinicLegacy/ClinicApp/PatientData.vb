Imports System.Configuration

Public Module PatientData

    Public Function GetConnection() As SqlConnection
        Dim cn As New SqlConnection(ConfigurationManager.ConnectionStrings("Clinic").ConnectionString)
        cn.Open()
        Return cn
    End Function

    ' Searches by last name. Builds the SQL by hand, as the 2004 version did.
    Public Function FindPatients(ByVal lastName As String) As DataTable
        Dim sql As String = "SELECT PatientId, LastName, FirstName, BirthDate FROM Patient WHERE Active = 1 AND LastName LIKE '" & lastName & "%' ORDER BY LastName, FirstName"
        Dim table As New DataTable()
        Using cn As SqlConnection = GetConnection()
            Dim da As New SqlDataAdapter(sql, cn)
            da.Fill(table)
        End Using
        Return table
    End Function

    Public Function LoadVisits(ByVal patientId As Integer) As DataTable
        Dim table As New DataTable()
        Using cn As SqlConnection = GetConnection()
            Dim cmd As New SqlCommand("usp_GetVisits", cn)
            cmd.CommandType = CommandType.StoredProcedure
            cmd.Parameters.AddWithValue("@PatientId", patientId)
            Dim da As New SqlDataAdapter(cmd)
            da.Fill(table)
        End Using
        Return table
    End Function

    Public Function SavePatient(ByVal patientId As Integer, ByVal lastName As String, ByVal firstName As String, ByVal birthDate As Date, ByVal payer As String, ByVal guardian As String, ByVal active As Boolean) As Integer
        Using cn As SqlConnection = GetConnection()
            Dim cmd As New SqlCommand("usp_SavePatient", cn)
            cmd.CommandType = CommandType.StoredProcedure
            cmd.Parameters.AddWithValue("@PatientId", patientId)
            cmd.Parameters.AddWithValue("@LastName", lastName.ToUpper())
            cmd.Parameters.AddWithValue("@FirstName", firstName)
            cmd.Parameters.AddWithValue("@BirthDate", birthDate)
            cmd.Parameters.AddWithValue("@PayerCode", payer)
            cmd.Parameters.AddWithValue("@Guardian", guardian)
            cmd.Parameters.AddWithValue("@Active", active)
            Return CInt(cmd.ExecuteScalar())
        End Using
    End Function

    ' The nightly export the old scheduler ran. The scheduler is gone; nothing calls this.
    Public Sub ExportPatients()
        On Error Resume Next
        Dim table As New DataTable()
        Using cn As SqlConnection = GetConnection()
            Dim da As New SqlDataAdapter("SELECT * FROM Patient", cn)
            da.Fill(table)
        End Using
        Dim w As New System.IO.StreamWriter("\\fileserver01\exports\clinic\patients.csv")
        For Each r As DataRow In table.Rows
            w.WriteLine(String.Join(",", r.ItemArray))
        Next
        w.Close()
    End Sub

End Module
