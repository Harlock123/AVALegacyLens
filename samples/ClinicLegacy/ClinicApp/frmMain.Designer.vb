<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()> _
Partial Class frmMain
    Inherits System.Windows.Forms.Form

    Private components As System.ComponentModel.IContainer

    <System.Diagnostics.DebuggerStepThrough()> _
    Private Sub InitializeComponent()
        Me.lblTitle = New System.Windows.Forms.Label()
        Me.btnPatients = New System.Windows.Forms.Button()
        Me.btnBilling = New System.Windows.Forms.Button()
        Me.btnExit = New System.Windows.Forms.Button()
        Me.SuspendLayout()
        '
        'lblTitle
        '
        Me.lblTitle.Font = New System.Drawing.Font("Microsoft Sans Serif", 14.0!, System.Drawing.FontStyle.Bold)
        Me.lblTitle.Location = New System.Drawing.Point(24, 16)
        Me.lblTitle.Name = "lblTitle"
        Me.lblTitle.Size = New System.Drawing.Size(320, 28)
        Me.lblTitle.TabIndex = 0
        Me.lblTitle.Text = "Clinic Front Desk"
        '
        'btnPatients
        '
        Me.btnPatients.Location = New System.Drawing.Point(24, 64)
        Me.btnPatients.Name = "btnPatients"
        Me.btnPatients.Size = New System.Drawing.Size(150, 32)
        Me.btnPatients.TabIndex = 1
        Me.btnPatients.Text = "&Patients"
        '
        'btnBilling
        '
        Me.btnBilling.Location = New System.Drawing.Point(194, 64)
        Me.btnBilling.Name = "btnBilling"
        Me.btnBilling.Size = New System.Drawing.Size(150, 32)
        Me.btnBilling.TabIndex = 2
        Me.btnBilling.Text = "&Billing"
        '
        'btnExit
        '
        Me.btnExit.Location = New System.Drawing.Point(194, 112)
        Me.btnExit.Name = "btnExit"
        Me.btnExit.Size = New System.Drawing.Size(150, 32)
        Me.btnExit.TabIndex = 3
        Me.btnExit.Text = "E&xit"
        '
        'frmMain
        '
        Me.ClientSize = New System.Drawing.Size(370, 164)
        Me.Controls.Add(Me.lblTitle)
        Me.Controls.Add(Me.btnPatients)
        Me.Controls.Add(Me.btnBilling)
        Me.Controls.Add(Me.btnExit)
        Me.Name = "frmMain"
        Me.Text = "Clinic"
        Me.ResumeLayout(False)
    End Sub

    Friend WithEvents lblTitle As System.Windows.Forms.Label
    Friend WithEvents btnPatients As System.Windows.Forms.Button
    Friend WithEvents btnBilling As System.Windows.Forms.Button
    Friend WithEvents btnExit As System.Windows.Forms.Button
End Class
