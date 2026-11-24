Imports ClinicUtil

Public Class frmPatient

    Private _patientId As Integer = 0

    Private Sub frmPatient_Load(ByVal sender As Object, ByVal e As EventArgs) Handles MyBase.Load
        cboPayer.Items.AddRange(New String() {"MEDICAID", "MEDICARE", "COMMERCIAL", "SELF-PAY"})
        cboPayer.SelectedIndex = 0
    End Sub

    Private Sub btnLookup_Click(ByVal sender As Object, ByVal e As EventArgs) Handles btnLookup.Click
        If txtLastName.Text.Trim().Length < 2 Then
            MessageBox.Show("Type at least two letters of the last name.")
            Exit Sub
        End If
        Dim found As DataTable = PatientData.FindPatients(txtLastName.Text)
        If found.Rows.Count = 0 Then
            MessageBox.Show("No patient found.")
            Exit Sub
        End If
        _patientId = CInt(found.Rows(0)("PatientId"))
        txtFirstName.Text = CStr(found.Rows(0)("FirstName"))
        dtpBirthDate.Value = CDate(found.Rows(0)("BirthDate"))
        grdVisits.DataSource = PatientData.LoadVisits(_patientId)
    End Sub

    Private Sub btnSave_Click(ByVal sender As Object, ByVal e As EventArgs) Handles btnSave.Click
        If Not ValidatePatient() Then Exit Sub
        _patientId = PatientData.SavePatient(_patientId, txtLastName.Text, txtFirstName.Text, dtpBirthDate.Value, cboPayer.Text, txtGuardian.Text, chkActive.Checked)
        MessageBox.Show("Saved.")
    End Sub

    Private Function ValidatePatient() As Boolean
        If txtLastName.Text.Trim() = "" Then
            MessageBox.Show("Last name is required.")
            Return False
        End If
        If dtpBirthDate.Value > DateTime.Today Then
            MessageBox.Show("Birth date cannot be in the future.")
            Return False
        End If
        If AgeCalculator.IsMinor(dtpBirthDate.Value) AndAlso txtGuardian.Text.Trim() = "" Then
            MessageBox.Show("A guardian is required for patients under 18.")
            Return False
        End If
        If cboPayer.Text = "MEDICAID" AndAlso AgeCalculator.Age(dtpBirthDate.Value) > 64 Then
            If MessageBox.Show("Medicaid patients over 64 are usually dual eligible. Save anyway?", "Payer", MessageBoxButtons.YesNo) = DialogResult.No Then
                Return False
            End If
        End If
        Return True
    End Function

End Class
