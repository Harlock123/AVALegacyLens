CREATE PROCEDURE dbo.usp_SavePatient
    @PatientId int, @LastName varchar(40), @FirstName varchar(30), @BirthDate date,
    @PayerCode varchar(12), @Guardian varchar(60), @Active bit
AS
BEGIN
    SET NOCOUNT ON;
    IF @BirthDate > GETDATE()
    BEGIN
        RAISERROR('Birth date cannot be in the future.', 16, 1);
        RETURN;
    END
    IF DATEDIFF(year, @BirthDate, GETDATE()) < 18 AND ISNULL(@Guardian, '') = ''
    BEGIN
        RAISERROR('Patients under 18 need a guardian.', 16, 1);
        RETURN;
    END
    IF @PatientId = 0
    BEGIN
        INSERT INTO dbo.Patient (LastName, FirstName, BirthDate, PayerCode, Guardian, Active)
        VALUES (@LastName, @FirstName, @BirthDate, @PayerCode, @Guardian, @Active);
        SET @PatientId = SCOPE_IDENTITY();
    END
    ELSE
    BEGIN
        UPDATE dbo.Patient
           SET LastName = @LastName, FirstName = @FirstName, BirthDate = @BirthDate,
               PayerCode = @PayerCode, Guardian = @Guardian, Active = @Active
         WHERE PatientId = @PatientId;
    END
    INSERT INTO dbo.AuditLog (TableName, KeyValue, Action) VALUES ('Patient', @PatientId, 'SAVE');
    SELECT @PatientId;
END
GO
CREATE PROCEDURE dbo.usp_GetVisits @PatientId int
AS
BEGIN
    SELECT v.VisitId, v.VisitDate, v.Clinician, v.Reason
      FROM dbo.Visit v
     WHERE v.PatientId = @PatientId
     ORDER BY v.VisitDate DESC;
END
GO
CREATE PROCEDURE dbo.usp_PostCharge
    @PatientId int, @ProcedureCode char(5), @Amount decimal(9,2), @Copay decimal(9,2)
AS
BEGIN
    DECLARE @Active bit;
    SELECT @Active = Active FROM dbo.Patient WHERE PatientId = @PatientId;
    IF @Active = 0
    BEGIN
        RAISERROR('Charges cannot be posted to an inactive patient.', 16, 1);
        RETURN;
    END
    INSERT INTO dbo.Charge (PatientId, ProcedureCode, Amount, Copay)
    VALUES (@PatientId, @ProcedureCode, @Amount,
            CASE WHEN @Copay > @Amount THEN @Amount ELSE @Copay END);
END
GO
CREATE VIEW dbo.vw_OpenBalances
AS
SELECT p.PatientId, p.LastName, p.FirstName, SUM(c.Copay) AS CopayDue
  FROM dbo.Patient p
  JOIN dbo.Charge c ON c.PatientId = p.PatientId
 WHERE p.Active = 1
 GROUP BY p.PatientId, p.LastName, p.FirstName;
GO
CREATE TRIGGER dbo.trg_Charge_Audit ON dbo.Charge AFTER INSERT
AS
BEGIN
    INSERT INTO dbo.AuditLog (TableName, KeyValue, Action)
    SELECT 'Charge', i.ChargeId, 'POST' FROM inserted i;
END
GO
-- Purges visits older than seven years. Once run by hand each January; no longer called.
CREATE PROCEDURE dbo.usp_PurgeOldVisits
AS
BEGIN
    DECLARE @sql nvarchar(400) = N'DELETE FROM dbo.Visit WHERE VisitDate < ''' + CONVERT(varchar(10), DATEADD(year, -7, GETDATE()), 120) + N'''';
    EXEC (@sql);
END
GO
