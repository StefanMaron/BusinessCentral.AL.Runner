codeunit 50914 "Assert 130000 Tests"
{
    Subtype = Test;

    var
        StringHelper: Codeunit "String Helper";
        Assert: Codeunit Assert;

    [Test]
    procedure TestReverseString()
    begin
        // [GIVEN/WHEN] Reversing 'hello'
        // [THEN] Result should be 'olleh'
        Assert.AreEqual('olleh', StringHelper.Reverse('hello'), 'Reverse of hello should be olleh');
    end;

    [Test]
    procedure TestIsPalindromeTrue()
    begin
        // [GIVEN/WHEN] Checking if 'racecar' is a palindrome
        // [THEN] Should return true
        Assert.IsTrue(StringHelper.IsPalindrome('racecar'), 'racecar should be a palindrome');
    end;

    [Test]
    procedure TestIsPalindromeFalse()
    begin
        // [GIVEN/WHEN] Checking if 'hello' is a palindrome
        // [THEN] Should return false
        Assert.IsFalse(StringHelper.IsPalindrome('hello'), 'hello should not be a palindrome');
    end;

    [Test]
    procedure TestAssertAreNotEqual()
    begin
        // [GIVEN/WHEN] Two different values
        // [THEN] AreNotEqual should pass
        Assert.AreNotEqual('abc', 'xyz', 'abc and xyz should not be equal');
    end;
}
