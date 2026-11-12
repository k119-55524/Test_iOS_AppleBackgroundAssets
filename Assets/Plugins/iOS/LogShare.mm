// Нативное iOS-меню «Поделиться» (UIActivityViewController) для файлов лога.
// Вызывается из LogManager.cs через [DllImport("__Internal")].
#import <UIKit/UIKit.h>

extern UIViewController* UnityGetGLViewController();

extern "C" void LogShare_ShareFiles(const char** paths, int count, const char* subject)
{
    NSMutableArray* items = [NSMutableArray array];
    for (int i = 0; i < count; i++)
    {
        if (paths[i] == NULL)
            continue;
        NSString* path = [NSString stringWithUTF8String:paths[i]];
        if ([[NSFileManager defaultManager] fileExistsAtPath:path])
            [items addObject:[NSURL fileURLWithPath:path]];
    }

    if (items.count == 0)
        return;

    NSString* subjectText = subject != NULL ? [NSString stringWithUTF8String:subject] : nil;

    dispatch_async(dispatch_get_main_queue(), ^{
        UIActivityViewController* activity =
            [[UIActivityViewController alloc] initWithActivityItems:items applicationActivities:nil];
        if (subjectText != nil)
            [activity setValue:subjectText forKey:@"subject"]; // тема письма в Почте

        UIViewController* root = UnityGetGLViewController();
        while (root.presentedViewController != nil)
            root = root.presentedViewController;

        // На iPad меню показывается как popover — нужен якорь.
        UIPopoverPresentationController* popover = activity.popoverPresentationController;
        if (popover != nil)
        {
            popover.sourceView = root.view;
            CGRect b = root.view.bounds;
            popover.sourceRect = CGRectMake(b.size.width * 0.75, b.size.height * 0.08, 1, 1);
        }

        [root presentViewController:activity animated:YES completion:nil];
    });
}
