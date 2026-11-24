Attribute VB_Name = "modClaims"
Option Explicit

Public gConn As ADODB.Connection
Public gUserName As String

Private Declare Function GetTickCount Lib "kernel32" () As Long

Public Sub OpenDb()
    Set gConn = New ADODB.Connection
    gConn.Open "Provider=SQLOLEDB;Data Source=clinicdb01;Initial Catalog=Clinic;User ID=claims;Password=Winter2003"
End Sub

' Saves a claim; claims for inactive patients are refused by usp_PostCharge.
Public Sub SaveClaim(c As clsClaim)
    On Error Resume Next
    Dim sql As String
    sql = "INSERT INTO Claim (PatientId, Amount, Status) VALUES (" & c.PatientId & ", " & c.Amount & ", '" & c.Status & "')"
    gConn.Execute sql
    Dim cmd As New ADODB.Command
    Set cmd.ActiveConnection = gConn
    cmd.CommandType = adCmdStoredProc
    cmd.CommandText = "usp_PostCharge"
    cmd.Execute
End Sub

Public Function CountToday() As Long
    Dim rs As ADODB.Recordset
    Set rs = gConn.Execute("SELECT COUNT(*) FROM Claim WHERE EnteredOn >= CONVERT(date, GETDATE())")
    CountToday = rs.Fields(0).Value
End Function

' Printed labels for the old dot-matrix printer. Nothing calls this.
Public Sub PrintClaimLabel(ByVal claimId As Long)
    Dim t As Long
    t = GetTickCount()
    Printer.Print "Claim " & claimId
    Printer.EndDoc
End Sub
