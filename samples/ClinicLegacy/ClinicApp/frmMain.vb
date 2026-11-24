Public Class frmMain

    Private Sub btnPatients_Click(ByVal sender As Object, ByVal e As EventArgs) Handles btnPatients.Click
        Dim f As New frmPatient()
        f.ShowDialog(Me)
    End Sub

    Private Sub btnBilling_Click(ByVal sender As Object, ByVal e As EventArgs) Handles btnBilling.Click
        Dim f As New frmBilling()
        f.ShowDialog(Me)
    End Sub

    Private Sub btnExit_Click(ByVal sender As Object, ByVal e As EventArgs) Handles btnExit.Click
        Me.Close()
    End Sub

    ' Kept from the 2009 version; nothing calls it any more.
    Private Sub ShowSplash()
        MessageBox.Show("Clinic Front Desk 2.1")
    End Sub

End Class
