Public Class BillingRules

    Public Const SupervisorLimit As Decimal = 5000D

    ' Posts a charge and returns the copay the patient owes today.
    Public Function PostCharge(ByVal patientId As Integer, ByVal procedureCode As String, ByVal amount As Decimal) As Decimal
        If amount > SupervisorLimit Then
            If MessageBox.Show("Charges over $5,000 need a supervisor. Has one approved it?", "Billing", MessageBoxButtons.YesNo) = DialogResult.No Then
                Throw New InvalidOperationException("A supervisor must approve charges over $5,000.")
            End If
        End If
        Dim payer As String = PayerOf(patientId)
        Dim copay As Decimal = CalculateCopay(payer, amount)
        Using cn As SqlConnection = PatientData.GetConnection()
            Dim cmd As New SqlCommand("usp_PostCharge", cn)
            cmd.CommandType = CommandType.StoredProcedure
            cmd.Parameters.AddWithValue("@PatientId", patientId)
            cmd.Parameters.AddWithValue("@ProcedureCode", procedureCode)
            cmd.Parameters.AddWithValue("@Amount", amount)
            cmd.Parameters.AddWithValue("@Copay", copay)
            cmd.ExecuteNonQuery()
        End Using
        Return copay
    End Function

    Public Function CalculateCopay(ByVal payer As String, ByVal amount As Decimal) As Decimal
        Select Case payer
            Case "MEDICAID"
                Return 0D
            Case "MEDICARE"
                Return Math.Round(amount * 0.2D, 2)
            Case "SELF-PAY"
                Return amount
            Case Else
                If amount < 100D Then Return amount
                Return 25D
        End Select
    End Function

    Private Function PayerOf(ByVal patientId As Integer) As String
        Using cn As SqlConnection = PatientData.GetConnection()
            Dim cmd As New SqlCommand("SELECT PayerCode FROM Patient WHERE PatientId = @Id", cn)
            cmd.Parameters.AddWithValue("@Id", patientId)
            Return CStr(cmd.ExecuteScalar())
        End Using
    End Function

    ' Rounding from before decimals were used. Not called.
    Public Function LegacyRound(ByVal value As Double) As Double
        Return Int(value * 100 + 0.5) / 100
    End Function

End Class
