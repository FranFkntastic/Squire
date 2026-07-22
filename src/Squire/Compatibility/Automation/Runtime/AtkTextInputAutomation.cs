using System;
using System.Globalization;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MarketMafioso.Automation.Runtime;

public static class AtkTextInputAutomation
{
    public static unsafe UiTextInputFocusResult FocusTextInput(
        AtkUnitBase* addon,
        AtkComponentInputBase* inputBase,
        AtkResNode* focusNode)
    {
        if (addon == null)
            throw new ArgumentNullException(nameof(addon));

        if (inputBase == null)
            throw new ArgumentNullException(nameof(inputBase));

        var focusSet = false;
        if (focusNode != null)
        {
            addon->Focus();
            addon->SetFocusNode(focusNode, setCursorFocusNode: true);
            addon->SetComponentFocusNode(&inputBase->AtkComponentBase);

            var stage = AtkStage.Instance();
            if (stage != null && stage->AtkInputManager != null)
                focusSet = stage->AtkInputManager->SetFocus(focusNode, addon, 0);
        }

        inputBase->IsActive = true;

        return new UiTextInputFocusResult(focusSet, inputBase->IsActive);
    }

    public static unsafe void SetEditableText(
        AtkComponentTextInput* input,
        AtkComponentInputBase* inputBase,
        string text)
    {
        if (input == null)
            throw new ArgumentNullException(nameof(input));

        if (inputBase == null)
            throw new ArgumentNullException(nameof(inputBase));

        input->SetText(text);
        inputBase->SelectionStart = text.Length;
        inputBase->SelectionEnd = text.Length;
        inputBase->CursorPos = text.Length;
    }

    public static unsafe string InvokeCallback(
        AtkUnitBase* addon,
        AtkComponentInputBase* inputBase,
        UiTextInputCallbackKind callback)
    {
        if (addon == null)
            throw new ArgumentNullException(nameof(addon));

        if (inputBase == null)
            throw new ArgumentNullException(nameof(inputBase));

        if (inputBase->Callback == null)
            return $"{callback}:Unavailable";

        var callbackType = callback == UiTextInputCallbackKind.TextChanged
            ? InputCallbackType.TextChanged
            : InputCallbackType.Enter;
        var callbackResult = inputBase->Callback(
            addon,
            callbackType,
            inputBase->RawString.StringPtr,
            inputBase->EvaluatedString.StringPtr,
            inputBase->CallbackEventKind);
        return $"{callback}:{callbackResult}";
    }

    public static bool IsSubmitAccepted(UiTextInputSubmitEvidence evidence)
    {
        return evidence.TargetVisible ||
               evidence.ResultVisible ||
               evidence.WorkInProgress ||
               evidence.ActivationSent;
    }

    public static unsafe string FormatNode(AtkResNode* node)
    {
        return node == null
            ? "null"
            : $"0x{FormatPointerValue(node)}#{node->NodeId.ToString(CultureInfo.InvariantCulture)}";
    }

    private static unsafe string FormatPointerValue(void* pointer)
    {
        return ((nuint)pointer).ToString("X", CultureInfo.InvariantCulture);
    }
}

public enum UiTextInputCallbackKind
{
    TextChanged,
    Enter,
}

public sealed record UiTextInputFocusResult(bool FocusSet, bool IsActive);

public sealed record UiTextInputSubmitEvidence(
    bool TargetVisible,
    bool ResultVisible,
    bool WorkInProgress,
    bool ActivationSent);
