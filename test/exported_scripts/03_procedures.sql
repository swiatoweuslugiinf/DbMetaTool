-- Procedury
CREATE PROCEDURE SP_GET_CUSTOMER_BALANCE (CUSTOMERID DM_SHORT, BALANCE DM_AMOUNT) AS
BEGIN
    SELECT c.balance
    FROM customers c
    WHERE c.customer_id = :customerID
    INTO :balance;
END

