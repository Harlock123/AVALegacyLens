Public Class frmBilling

    Private ReadOnly _rules As New BillingRules()

    Private Sub btnPost_Click(ByVal sender As Object, ByVal e As EventArgs) Handles btnPost.Click
        Dim amount As Decimal
        If Not Decimal.TryParse(txtAmount.Text, amount) OrElse amount <= 0 Then
            MessageBox.Show("Enter an amount greater than zero.")
            Exit Sub
        End If
        If txtProcedureCode.Text.Length <> 5 Then
            MessageBox.Show("Procedure codes are five characters.")
            Exit Sub
        End If
        Dim copay As Decimal = _rules.PostCharge(CInt(txtPatientId.Text), txtProcedureCode.Text, amount)
        lblCopay.Text = "Copay due: " & copay.ToString("C")
    End Sub

End Class
