<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()> _
Partial Class frmBilling
    Inherits System.Windows.Forms.Form

    Private components As System.ComponentModel.IContainer

    <System.Diagnostics.DebuggerStepThrough()> _
    Private Sub InitializeComponent()
        Me.lblPatientId = New System.Windows.Forms.Label()
        Me.txtPatientId = New System.Windows.Forms.TextBox()
        Me.lblAmount = New System.Windows.Forms.Label()
        Me.txtAmount = New System.Windows.Forms.TextBox()
        Me.lblCode = New System.Windows.Forms.Label()
        Me.txtProcedureCode = New System.Windows.Forms.TextBox()
        Me.btnPost = New System.Windows.Forms.Button()
        Me.lblCopay = New System.Windows.Forms.Label()
        Me.SuspendLayout()
        '
        'lblPatientId
        '
        Me.lblPatientId.Location = New System.Drawing.Point(16, 18)
        Me.lblPatientId.Name = "lblPatientId"
        Me.lblPatientId.Size = New System.Drawing.Size(100, 20)
        Me.lblPatientId.Text = "Patient ID"
        '
        'txtPatientId
        '
        Me.txtPatientId.Location = New System.Drawing.Point(122, 16)
        Me.txtPatientId.Name = "txtPatientId"
        Me.txtPatientId.Size = New System.Drawing.Size(120, 22)
        Me.txtPatientId.TabIndex = 0
        '
        'lblAmount
        '
        Me.lblAmount.Location = New System.Drawing.Point(16, 48)
        Me.lblAmount.Name = "lblAmount"
        Me.lblAmount.Size = New System.Drawing.Size(100, 20)
        Me.lblAmount.Text = "Amount"
        '
        'txtAmount
        '
        Me.txtAmount.Location = New System.Drawing.Point(122, 46)
        Me.txtAmount.Name = "txtAmount"
        Me.txtAmount.Size = New System.Drawing.Size(120, 22)
        Me.txtAmount.TabIndex = 1
        '
        'lblCode
        '
        Me.lblCode.Location = New System.Drawing.Point(16, 78)
        Me.lblCode.Name = "lblCode"
        Me.lblCode.Size = New System.Drawing.Size(100, 20)
        Me.lblCode.Text = "Procedure code"
        '
        'txtProcedureCode
        '
        Me.txtProcedureCode.Location = New System.Drawing.Point(122, 76)
        Me.txtProcedureCode.MaxLength = 5
        Me.txtProcedureCode.Name = "txtProcedureCode"
        Me.txtProcedureCode.Size = New System.Drawing.Size(80, 22)
        Me.txtProcedureCode.TabIndex = 2
        '
        'btnPost
        '
        Me.btnPost.Location = New System.Drawing.Point(122, 110)
        Me.btnPost.Name = "btnPost"
        Me.btnPost.Size = New System.Drawing.Size(120, 28)
        Me.btnPost.TabIndex = 3
        Me.btnPost.Text = "&Post charge"
        '
        'lblCopay
        '
        Me.lblCopay.Location = New System.Drawing.Point(16, 150)
        Me.lblCopay.Name = "lblCopay"
        Me.lblCopay.Size = New System.Drawing.Size(240, 20)
        '
        'frmBilling
        '
        Me.ClientSize = New System.Drawing.Size(280, 186)
        Me.Controls.Add(Me.lblPatientId)
        Me.Controls.Add(Me.txtPatientId)
        Me.Controls.Add(Me.lblAmount)
        Me.Controls.Add(Me.txtAmount)
        Me.Controls.Add(Me.lblCode)
        Me.Controls.Add(Me.txtProcedureCode)
        Me.Controls.Add(Me.btnPost)
        Me.Controls.Add(Me.lblCopay)
        Me.Name = "frmBilling"
        Me.Text = "Billing"
        Me.ResumeLayout(False)
    End Sub

    Friend WithEvents lblPatientId As System.Windows.Forms.Label
    Friend WithEvents txtPatientId As System.Windows.Forms.TextBox
    Friend WithEvents lblAmount As System.Windows.Forms.Label
    Friend WithEvents txtAmount As System.Windows.Forms.TextBox
    Friend WithEvents lblCode As System.Windows.Forms.Label
    Friend WithEvents txtProcedureCode As System.Windows.Forms.TextBox
    Friend WithEvents btnPost As System.Windows.Forms.Button
    Friend WithEvents lblCopay As System.Windows.Forms.Label
End Class
