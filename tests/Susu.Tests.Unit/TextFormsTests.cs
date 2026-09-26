using Susu.Contracts;
using Susu.Domain;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>F09.1 / DICT01: the PLAN 6.1 word-form rule that decides whether a card may use the dictionary.</summary>
public class TextFormsTests
{
    [Theory]
    // Latin word forms: letters, hyphen, apostrophe; trimmed; no whitespace; <= 32 characters.
    [InlineData("hello", TextForm.LatinWord)]
    [InlineData("  Hello \r\n", TextForm.LatinWord)]
    [InlineData("well-known", TextForm.LatinWord)]
    [InlineData("don't", TextForm.LatinWord)]
    [InlineData("rock’n’roll", TextForm.LatinWord)]
    [InlineData("café", TextForm.LatinWord)]
    [InlineData("naïve", TextForm.LatinWord)]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdef", TextForm.LatinWord)] // exactly 32
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefg", TextForm.Other)]    // 33
    // Chinese word forms: Han only, <= 4.
    [InlineData("好", TextForm.ChineseWord)]
    [InlineData("你好", TextForm.ChineseWord)]
    [InlineData("一帆风顺", TextForm.ChineseWord)]
    [InlineData(" 词典 ", TextForm.ChineseWord)]
    [InlineData("\U00020000", TextForm.ChineseWord)] // CJK Ext B
    [InlineData("中华人民共和国", TextForm.Phrase)]     // 7 Han, no punctuation
    [InlineData("我今天很高兴能见到你们大家", TextForm.Sentence)]
    // Phrases and sentences.
    [InlineData("good morning", TextForm.Phrase)]
    [InlineData("take off", TextForm.Phrase)]
    [InlineData("the quick brown fox jumps over the lazy dog", TextForm.Sentence)]
    [InlineData("Hello world.", TextForm.Sentence)]
    [InlineData("How are you?", TextForm.Sentence)]
    [InlineData("He said \"stop!\"", TextForm.Sentence)]
    [InlineData("你好吗？", TextForm.Sentence)]
    [InlineData("今天天气很好。", TextForm.Sentence)]
    // Punctuated text that is not a sentence.
    [InlineData("hello,", TextForm.Punctuated)]
    [InlineData("good!", TextForm.Punctuated)]
    [InlineData("\"hello\"", TextForm.Punctuated)]
    [InlineData("e.g.", TextForm.Punctuated)]
    [InlineData("好！", TextForm.Punctuated)]
    [InlineData("你好，世界", TextForm.Punctuated)]
    [InlineData("a, b", TextForm.Punctuated)]
    [InlineData("...", TextForm.Punctuated)]
    [InlineData("-", TextForm.Punctuated)]
    [InlineData("hello😀", TextForm.Punctuated)]
    // Mixed scripts, digits, other scripts, empty.
    [InlineData("hello世界", TextForm.Mixed)]
    [InlineData("iPhone 手机", TextForm.Mixed)]
    [InlineData("mp3", TextForm.Other)]
    [InlineData("2024", TextForm.Other)]
    [InlineData("第1名", TextForm.Other)]
    [InlineData("こんにちは", TextForm.Other)]
    [InlineData("привет", TextForm.Other)]
    [InlineData("", TextForm.Empty)]
    [InlineData("   \t", TextForm.Empty)]
    public void Classifies_per_PLAN_6_1(string text, TextForm expected) => Assert.Equal(expected, TextForms.Classify(text));

    [Fact]
    public void Null_is_empty() => Assert.Equal(TextForm.Empty, TextForms.Classify(null));

    [Theory]
    [InlineData("hello", true)]
    [InlineData("你好", true)]
    [InlineData("good morning", false)]
    [InlineData("hello,", false)]
    [InlineData("Hello world.", false)]
    [InlineData("hello世界", false)]
    [InlineData("中华人民共和国", false)]
    [InlineData("", false)]
    public void Only_word_forms_use_the_dictionary_when_one_is_configured(string text, bool wordForm)
    {
        Assert.Equal(wordForm, TextForms.UsesDictionary(text, dictionaryConfigured: true));
        Assert.False(TextForms.UsesDictionary(text, dictionaryConfigured: false)); // no dictionary service: never, no fake source
    }

    [Fact]
    public void Dictionary_word_is_the_trimmed_word_form_or_null()
    {
        Assert.Equal("hello", TextForms.DictionaryWord("  hello\n"));
        Assert.Equal("你好", TextForms.DictionaryWord(" 你好 "));
        Assert.Null(TextForms.DictionaryWord("hello world"));
        Assert.Null(TextForms.DictionaryWord(null));
    }

    [Fact]
    public void Empty_entry_is_a_successful_result_with_nothing_to_show()
    {
        Assert.True(DictionaryEntries.IsEmpty(new DictionaryResult("xyzzy", [], [])));
        Assert.True(DictionaryEntries.IsEmpty(new DictionaryResult("xyzzy", [], [], [], [])));
        Assert.False(DictionaryEntries.IsEmpty(new DictionaryResult("good", [new Phonetic("us", "ɡʊd")], [])));
        Assert.False(DictionaryEntries.IsEmpty(new DictionaryResult("good", [], [new PartOfSpeech("adj.", ["好的"])])));
        Assert.False(DictionaryEntries.IsEmpty(new DictionaryResult("good", [], [], [new WordForm("比较级", "better")])));
        Assert.False(DictionaryEntries.IsEmpty(new DictionaryResult("good", [], [], null, [new Example("Good job.", "干得好。")])));
    }
}
