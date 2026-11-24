VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
  Persistable = 0  'NotPersistable
  DataBindingBehavior = 0  'vbNone
  DataSourceBehavior  = 0  'vbNone
  MTSTransactionMode  = 0  'NotAnMTSObject
END
Attribute VB_Name = "clsClaim"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = True
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit

Private mPatientId As Long
Private mAmount As Currency
Private mStatus As String

Public Property Get PatientId() As Long
    PatientId = mPatientId
End Property

Public Property Let PatientId(ByVal v As Long)
    mPatientId = v
End Property

Public Property Get Amount() As Currency
    Amount = mAmount
End Property

Public Property Let Amount(ByVal v As Currency)
    mAmount = v
End Property

Public Property Get Status() As String
    Status = mStatus
End Property

Public Property Let Status(ByVal v As String)
    mStatus = v
End Property

' A claim needs a patient and an amount above zero; HELD claims need nothing more.
Public Function IsValid() As Boolean
    IsValid = False
    If mPatientId <= 0 Then Exit Function
    If mAmount <= 0 Or mAmount > 99999.99 Then Exit Function
    Select Case mStatus
        Case "NEW", "HELD"
            IsValid = True
        Case Else
            IsValid = False
    End Select
End Function
