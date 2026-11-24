VERSION 5.00
Object = "{5E9E78A0-531B-11CF-91F6-C2863C385E30}#1.0#0"; "MSFLXGRD.OCX"
Begin VB.Form frmClaims
   Caption         =   "Claims Entry"
   ClientHeight    =   4320
   ClientLeft      =   60
   ClientTop       =   345
   ClientWidth     =   6480
   LinkTopic       =   "Form1"
   ScaleHeight     =   4320
   ScaleWidth      =   6480
   StartUpPosition =   3  'Windows Default
   Begin VB.TextBox txtPatientId
      Height          =   315
      Left            =   1680
      MaxLength       =   9
      TabIndex        =   0
      Top             =   240
      Width           =   1335
   End
   Begin VB.TextBox txtAmount
      Height          =   315
      Left            =   1680
      TabIndex        =   1
      Top             =   720
      Width           =   1335
   End
   Begin VB.ComboBox cboStatus
      Height          =   315
      Left            =   1680
      Style           =   2  'Dropdown List
      TabIndex        =   2
      Top             =   1200
      Width           =   2055
   End
   Begin VB.CommandButton cmdSave
      Caption         =   "&Save Claim"
      Default         =   -1  'True
      Height          =   375
      Left            =   4680
      TabIndex        =   3
      Top             =   240
      Width           =   1455
   End
   Begin VB.CommandButton cmdCancel
      Cancel          =   -1  'True
      Caption         =   "Cancel"
      Height          =   375
      Left            =   4680
      TabIndex        =   4
      Top             =   720
      Width           =   1455
   End
   Begin VB.Frame fraTotals
      Caption         =   "Today"
      Height          =   1095
      Left            =   240
      TabIndex        =   5
      Top             =   1800
      Width           =   3495
      Begin VB.Label lblTotal
         Caption         =   "0 claims"
         Height          =   255
         Left            =   240
         TabIndex        =   6
         Top             =   360
         Width           =   2895
      End
   End
   Begin MSFlexGridLib.MSFlexGrid grdRecent
      Height          =   1215
      Left            =   240
      TabIndex        =   7
      Top             =   3000
      Width           =   5895
      _ExtentX        =   10398
      _ExtentY        =   2143
      _Version        =   393216
   End
   Begin VB.Label lblPatient
      Caption         =   "Patient ID"
      Height          =   255
      Index           =   0
      Left            =   240
      TabIndex        =   8
      Top             =   285
      Width           =   1335
   End
   Begin VB.Label lblPatient
      Caption         =   "Amount"
      Height          =   255
      Index           =   1
      Left            =   240
      TabIndex        =   9
      Top             =   765
      Width           =   1335
   End
End
Attribute VB_Name = "frmClaims"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = True
Attribute VB_Exposed = False
Option Explicit

Private Sub Form_Load()
    cboStatus.AddItem "NEW"
    cboStatus.AddItem "HELD"
    cboStatus.ListIndex = 0
    OpenDb
    ShowTotal
End Sub

Private Sub cmdSave_Click()
    Dim c As clsClaim
    If Not IsNumeric(txtAmount.Text) Then
        MsgBox "The amount must be a number."
        Exit Sub
    End If
    If Val(txtAmount.Text) > 10000 Then
        MsgBox "Claims over $10,000 go to the review queue.", vbInformation
        cboStatus.Text = "HELD"
    End If
    Set c = New clsClaim
    c.PatientId = CLng(txtPatientId.Text)
    c.Amount = CCur(txtAmount.Text)
    c.Status = cboStatus.Text
    If c.IsValid Then
        SaveClaim c
        ShowTotal
    End If
End Sub

Private Sub cmdCancel_Click()
    Unload Me
End Sub

Private Sub ShowTotal()
    lblTotal.Caption = CountToday() & " claims"
End Sub
