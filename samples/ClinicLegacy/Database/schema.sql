-- Clinic database, as deployed in 2011. Invented sample data model.
CREATE TABLE dbo.Payer (
    PayerCode    varchar(12)  NOT NULL PRIMARY KEY,
    Name         varchar(60)  NOT NULL,
    CopayPercent decimal(5,2) NOT NULL DEFAULT 0
);
GO
CREATE TABLE dbo.Patient (
    PatientId  int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    LastName   varchar(40) NOT NULL,
    FirstName  varchar(30) NOT NULL,
    BirthDate  date        NOT NULL,
    PayerCode  varchar(12) NOT NULL REFERENCES dbo.Payer (PayerCode),
    Guardian   varchar(60) NULL,
    Active     bit         NOT NULL DEFAULT 1,
    CONSTRAINT CK_Patient_BirthDate CHECK (BirthDate <= GETDATE())
);
GO
CREATE TABLE dbo.Visit (
    VisitId     int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    PatientId   int      NOT NULL REFERENCES dbo.Patient (PatientId),
    VisitDate   datetime NOT NULL,
    Clinician   varchar(60) NULL,
    Reason      varchar(200) NULL
);
GO
CREATE TABLE dbo.Charge (
    ChargeId      int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    PatientId     int          NOT NULL REFERENCES dbo.Patient (PatientId),
    ProcedureCode char(5)      NOT NULL,
    Amount        decimal(9,2) NOT NULL,
    Copay         decimal(9,2) NOT NULL,
    PostedOn      datetime     NOT NULL DEFAULT GETDATE(),
    CONSTRAINT CK_Charge_Amount CHECK (Amount > 0)
);
GO
CREATE TABLE dbo.AuditLog (
    AuditId   int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    TableName varchar(40)  NOT NULL,
    KeyValue  int          NOT NULL,
    Action    varchar(10)  NOT NULL,
    ChangedOn datetime     NOT NULL DEFAULT GETDATE()
);
GO
-- Claims entered by the VB6 claims screen.
CREATE TABLE dbo.Claim (
    ClaimId   int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    PatientId int          NOT NULL REFERENCES dbo.Patient (PatientId),
    Amount    decimal(9,2) NOT NULL,
    Status    varchar(8)   NOT NULL,
    EnteredOn datetime     NOT NULL DEFAULT GETDATE()
);
GO
-- The fax queue from the 2006 referral system. Nothing reads or writes it now.
CREATE TABLE dbo.FaxQueue (
    FaxId    int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Number   varchar(20) NOT NULL,
    Document varbinary(max) NULL
);
GO
