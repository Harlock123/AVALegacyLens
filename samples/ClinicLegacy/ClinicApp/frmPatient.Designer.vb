<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()> _
Partial Class frmPatient
    Inherits System.Windows.Forms.Form

    Private components As System.ComponentModel.IContainer

    <System.Diagnostics.DebuggerStepThrough()> _
    Private Sub InitializeComponent()
        Me.lblLastName = New System.Windows.Forms.Label()
        Me.txtLastName = New System.Windows.Forms.TextBox()
        Me.lblFirstName = New System.Windows.Forms.Label()
        Me.txtFirstName = New System.Windows.Forms.TextBox()
        Me.lblBirthDate = New System.Windows.Forms.Label()
        Me.dtpBirthDate = New System.Windows.Forms.DateTimePicker()
        Me.lblPayer = New System.Windows.Forms.Label()
        Me.cboPayer = New System.Windows.Forms.ComboBox()
        Me.txtGuardian = New System.Windows.Forms.TextBox()
        Me.chkActive = New System.Windows.Forms.CheckBox()
        Me.btnLookup = New System.Windows.Forms.Button()
        Me.btnSave = New System.Windows.Forms.Button()
        Me.grdVisits = New System.Windows.Forms.DataGridView()
        Me.SuspendLayout()
        '
        'lblLastName
        '
        Me.lblLastName.Location = New System.Drawing.Point(16, 18)
        Me.lblLastName.Name = "lblLastName"
        Me.lblLastName.Size = New System.Drawing.Size(90, 20)
        Me.lblLastName.Text = "Last name"
        '
        'txtLastName
        '
        Me.txtLastName.Location = New System.Drawing.Point(112, 16)
        Me.txtLastName.MaxLength = 40
        Me.txtLastName.Name = "txtLastName"
        Me.txtLastName.Size = New System.Drawing.Size(200, 22)
        Me.txtLastName.TabIndex = 0
        '
        'lblFirstName
        '
        Me.lblFirstName.Location = New System.Drawing.Point(16, 48)
        Me.lblFirstName.Name = "lblFirstName"
        Me.lblFirstName.Size = New System.Drawing.Size(90, 20)
        Me.lblFirstName.Text = "First name"
        '
        'txtFirstName
        '
        Me.txtFirstName.Location = New System.Drawing.Point(112, 46)
        Me.txtFirstName.MaxLength = 30
        Me.txtFirstName.Name = "txtFirstName"
        Me.txtFirstName.Size = New System.Drawing.Size(200, 22)
        Me.txtFirstName.TabIndex = 1
        '
        'lblBirthDate
        '
        Me.lblBirthDate.Location = New System.Drawing.Point(16, 78)
        Me.lblBirthDate.Name = "lblBirthDate"
        Me.lblBirthDate.Size = New System.Drawing.Size(90, 20)
        Me.lblBirthDate.Text = "Birth date"
        '
        'dtpBirthDate
        '
        Me.dtpBirthDate.Format = System.Windows.Forms.DateTimePickerFormat.Short
        Me.dtpBirthDate.Location = New System.Drawing.Point(112, 76)
        Me.dtpBirthDate.Name = "dtpBirthDate"
        Me.dtpBirthDate.Size = New System.Drawing.Size(120, 22)
        Me.dtpBirthDate.TabIndex = 2
        '
        'lblPayer
        '
        Me.lblPayer.Location = New System.Drawing.Point(16, 108)
        Me.lblPayer.Name = "lblPayer"
        Me.lblPayer.Size = New System.Drawing.Size(90, 20)
        Me.lblPayer.Text = "Payer"
        '
        'cboPayer
        '
        Me.cboPayer.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList
        Me.cboPayer.Location = New System.Drawing.Point(112, 106)
        Me.cboPayer.Name = "cboPayer"
        Me.cboPayer.Size = New System.Drawing.Size(200, 22)
        Me.cboPayer.TabIndex = 3
        '
        'txtGuardian
        '
        Me.txtGuardian.Location = New System.Drawing.Point(112, 136)
        Me.txtGuardian.Name = "txtGuardian"
        Me.txtGuardian.Size = New System.Drawing.Size(200, 22)
        Me.txtGuardian.TabIndex = 4
        '
        'chkActive
        '
        Me.chkActive.Checked = True
        Me.chkActive.Location = New System.Drawing.Point(112, 166)
        Me.chkActive.Name = "chkActive"
        Me.chkActive.Size = New System.Drawing.Size(100, 20)
        Me.chkActive.TabIndex = 5
        Me.chkActive.Text = "Active"
        '
        'btnLookup
        '
        Me.btnLookup.Location = New System.Drawing.Point(330, 14)
        Me.btnLookup.Name = "btnLookup"
        Me.btnLookup.Size = New System.Drawing.Size(90, 26)
        Me.btnLookup.TabIndex = 6
        Me.btnLookup.Text = "&Lookup"
        '
        'btnSave
        '
        Me.btnSave.Location = New System.Drawing.Point(330, 162)
        Me.btnSave.Name = "btnSave"
        Me.btnSave.Size = New System.Drawing.Size(90, 26)
        Me.btnSave.TabIndex = 7
        Me.btnSave.Text = "&Save"
        '
        'grdVisits
        '
        Me.grdVisits.Location = New System.Drawing.Point(16, 200)
        Me.grdVisits.Name = "grdVisits"
        Me.grdVisits.ReadOnly = True
        Me.grdVisits.Size = New System.Drawing.Size(404, 150)
        Me.grdVisits.TabIndex = 8
        '
        'frmPatient
        '
        Me.ClientSize = New System.Drawing.Size(440, 366)
        Me.Controls.Add(Me.lblLastName)
        Me.Controls.Add(Me.txtLastName)
        Me.Controls.Add(Me.lblFirstName)
        Me.Controls.Add(Me.txtFirstName)
        Me.Controls.Add(Me.lblBirthDate)
        Me.Controls.Add(Me.dtpBirthDate)
        Me.Controls.Add(Me.lblPayer)
        Me.Controls.Add(Me.cboPayer)
        Me.Controls.Add(Me.txtGuardian)
        Me.Controls.Add(Me.chkActive)
        Me.Controls.Add(Me.btnLookup)
        Me.Controls.Add(Me.btnSave)
        Me.Controls.Add(Me.grdVisits)
        Me.Name = "frmPatient"
        Me.Text = "Patient"
        Me.ResumeLayout(False)
    End Sub

    Friend WithEvents lblLastName As System.Windows.Forms.Label
    Friend WithEvents txtLastName As System.Windows.Forms.TextBox
    Friend WithEvents lblFirstName As System.Windows.Forms.Label
    Friend WithEvents txtFirstName As System.Windows.Forms.TextBox
    Friend WithEvents lblBirthDate As System.Windows.Forms.Label
    Friend WithEvents dtpBirthDate As System.Windows.Forms.DateTimePicker
    Friend WithEvents lblPayer As System.Windows.Forms.Label
    Friend WithEvents cboPayer As System.Windows.Forms.ComboBox
    Friend WithEvents txtGuardian As System.Windows.Forms.TextBox
    Friend WithEvents chkActive As System.Windows.Forms.CheckBox
    Friend WithEvents btnLookup As System.Windows.Forms.Button
    Friend WithEvents btnSave As System.Windows.Forms.Button
    Friend WithEvents grdVisits As System.Windows.Forms.DataGridView
End Class
